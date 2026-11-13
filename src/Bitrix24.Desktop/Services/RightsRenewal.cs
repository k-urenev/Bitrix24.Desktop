using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.Core.Auth;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Bitrix24.Desktop.Services;

internal static class RightsRenewal
{
    public static async Task TryAsync(SessionHolder sessions, BitrixOAuthClient oauth, CancellationToken cancellationToken)
    {
        var session = sessions.Current;
        if (string.IsNullOrWhiteSpace(session.PortalDomain)
            || string.IsNullOrWhiteSpace(session.ClientId)
            || string.IsNullOrWhiteSpace(session.ClientSecret)
            || string.IsNullOrWhiteSpace(session.Login)
            || string.IsNullOrWhiteSpace(session.Password))
            return;

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var issued = new TaskCompletionSource<(string Code, string? Server)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new Window
        {
            Width = 480,
            Height = 320,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -20000,
            Top = -20000
        };
        var browser = new WebView2();
        window.Content = new Grid { Children = { browser } };

        void OnStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (!TryReadCode(args.Uri, state, out var code, out var server))
                return;
            args.Cancel = true;
            issued.TrySetResult((code, server));
        }

        var filled = "";
        async void OnReady(object? sender, CoreWebView2DOMContentLoadedEventArgs args)
        {
            if (browser.CoreWebView2 is null || issued.Task.IsCompleted)
                return;
            var uri = browser.CoreWebView2.Source;
            if (string.Equals(uri, filled, StringComparison.Ordinal))
                return;
            filled = uri;
            try
            {
                await browser.CoreWebView2.ExecuteScriptAsync(FillScript(session.Login, session.Password));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                FileLog.Write("Обновление прав: страница входа не заполнилась (" + exception.GetType().Name + ").");
            }
        }

        try
        {
            window.Show();
            var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.Create().WebViewFolder);
            await browser.EnsureCoreWebView2Async(environment);
            browser.CoreWebView2.NavigationStarting += OnStarting;
            browser.CoreWebView2.DOMContentLoaded += OnReady;
            browser.Source = new Uri(BitrixOAuthClient.AuthorizeUrl(session.PortalDomain, session.ClientId, state));

            var completed = await Task.WhenAny(issued.Task, Task.Delay(TimeSpan.FromSeconds(25), cancellationToken));
            if (completed != issued.Task)
            {
                FileLog.Write("Обновление прав при открытии не успело. Остаётся прежний токен.");
                return;
            }

            var (code, server) = await issued.Task;
            var updated = await oauth.ExchangeCodeAsync(session.Clone(), code, server, cancellationToken);
            sessions.Replace(updated);
            FileLog.Write("Обновление прав при открытии: токен получен заново.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            FileLog.Write("Обновление прав при открытии не удалось (" + exception.GetType().Name + ").");
        }
        finally
        {
            if (browser.CoreWebView2 is not null)
            {
                browser.CoreWebView2.NavigationStarting -= OnStarting;
                browser.CoreWebView2.DOMContentLoaded -= OnReady;
            }

            window.Close();
        }
    }

    private static string FillScript(string login, string password)
    {
        var loginJson = JsonSerializer.Serialize(login);
        var passwordJson = JsonSerializer.Serialize(password);
        return
            "(function(){" +
            "var login=" + loginJson + ";" +
            "var password=" + passwordJson + ";" +
            "var user=document.querySelector('input[name=\"USER_LOGIN\"]');" +
            "var pass=document.querySelector('input[name=\"USER_PASSWORD\"]');" +
            "if(user&&login&&!user.value){user.value=login;user.dispatchEvent(new Event('input',{bubbles:true}));}" +
            "if(pass&&password&&!pass.value){pass.value=password;pass.dispatchEvent(new Event('input',{bubbles:true}));}" +
            "if(user&&pass&&user.value&&pass.value){" +
            "var submit=(pass.form&&pass.form.querySelector('[type=\"submit\"]'))||document.querySelector('input[name=\"Login\"],button[type=\"submit\"]');" +
            "if(submit){submit.click();return;}}" +
            "var allow=document.querySelector('#authorize,button[type=\"submit\"],input[type=\"submit\"],.ui-btn-success');" +
            "if(allow)allow.click();" +
            "})();";
    }

    private static bool TryReadCode(string rawUri, string state, out string code, out string? serverDomain)
    {
        code = "";
        serverDomain = null;
        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri))
            return false;

        var query = ParseQuery(uri.Query);
        var fragment = ParseQuery(uri.Fragment);
        query.TryGetValue("code", out var queryCode);
        fragment.TryGetValue("code", out var fragmentCode);
        code = queryCode ?? fragmentCode ?? "";
        if (code.Length == 0)
            return false;

        var actual = query.TryGetValue("state", out var queryState) ? queryState : fragment.GetValueOrDefault("state");
        if (!string.Equals(actual, state, StringComparison.Ordinal))
            return false;

        if (query.TryGetValue("server_domain", out var domain) || fragment.TryGetValue("server_domain", out domain))
            serverDomain = domain;
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var text = raw.TrimStart('?', '#');
        if (text.Length == 0)
            return result;

        foreach (var part in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? part : part[..separator]);
            var value = separator < 0 ? "" : Uri.UnescapeDataString(part[(separator + 1)..]);
            result[key] = value;
        }

        return result;
    }
}
