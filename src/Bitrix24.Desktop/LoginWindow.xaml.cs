using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.Core.Auth;
using Microsoft.Web.WebView2.Core;

namespace Bitrix24.Desktop;

public partial class LoginWindow : Window
{
    private readonly SessionHolder _sessions;
    private readonly Func<string, string, string, string, string?, CancellationToken, Task> _complete;
    private readonly CancellationTokenSource _cancellation = new();
    private string _state = "";
    private string _portal = "";
    private string _clientId = "";
    private string _clientSecret = "";
    private string _login = "";
    private string _password = "";
    private string _filledUri = "";
    private bool _exchangeStarted;
    private Task? _browserReady;

    public LoginWindow(
        SessionHolder sessions,
        string? initialError,
        Func<string, string, string, string, string?, CancellationToken, Task> complete)
    {
        _sessions = sessions;
        _complete = complete;
        InitializeComponent();
        SourceInitialized += (_, _) => WindowFit.Apply(this);
        Loaded += (_, _) => WindowFit.Apply(this);
        var session = sessions.Current;
        PortalBox.Text = session.PortalDomain;
        ClientIdBox.Text = session.ClientId;
        SecretBox.Password = session.ClientSecret;
        LoginBox.Text = session.Login;
        LoginPasswordBox.Password = session.Password;
        if (!string.IsNullOrWhiteSpace(initialError))
            ShowError(initialError);
    }

    protected override void OnClosed(EventArgs e)
    {
        _cancellation.Cancel();
        try
        {
            Browser.Dispose();
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }

        base.OnClosed(e);
    }

    private static bool HasKeys(string portal, string clientId, string secret) =>
        !string.IsNullOrWhiteSpace(portal)
        && !string.IsNullOrWhiteSpace(clientId)
        && !string.IsNullOrWhiteSpace(secret);

    private async void OnSignInClick(object sender, RoutedEventArgs e)
    {
        if (!PortalAddress.TryNormalize(PortalBox.Text, out var host, out var error))
        {
            ShowError(string.IsNullOrWhiteSpace(PortalBox.Text)
                ? "Укажите адрес портала."
                : error);
            return;
        }

        var clientId = ClientIdBox.Text.Trim();
        var secret = SecretBox.Password.Trim();
        if (!HasKeys(host, clientId, secret))
        {
            ShowError("Заполните код приложения и секретный ключ. Это не логин и не пароль.");
            return;
        }

        var login = LoginBox.Text.Trim();
        var password = LoginPasswordBox.Password;
        var draft = _sessions.Current.Clone();
        draft.PortalDomain = host;
        draft.ClientId = clientId;
        draft.ClientSecret = secret;
        draft.Login = login;
        draft.Password = password;
        _sessions.Replace(draft);

        _portal = host;
        _clientId = clientId;
        _clientSecret = secret;
        _login = login;
        _password = password;
        _state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _exchangeStarted = false;
        _filledUri = "";
        ShowError(null);

        try
        {
            SignInButton.IsEnabled = false;
            if (!await PortalResolvesAsync(host))
            {
                if (IsLoaded)
                    ShowError("IP-адрес портала не найден. Проверьте написание.");
                return;
            }

            ShowBrowser();
            await EnsureBrowserAsync();
            if (!IsLoaded || Browser.CoreWebView2 is null)
                return;
            Browser.Source = new Uri(BitrixOAuthClient.AuthorizeUrl(host, clientId, _state));
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
            if (!IsLoaded)
                return;
            ShowForm(exception is WebView2RuntimeNotFoundException
                ? "Не найден WebView2. Установите среду выполнения Microsoft Edge WebView2 и откройте приложение снова."
                : "Не удалось открыть страницу входа. Нажмите «Войти» ещё раз.");
        }
        finally
        {
            if (IsLoaded)
                SignInButton.IsEnabled = true;
        }
    }

    private static async Task<bool> PortalResolvesAsync(string host)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            return addresses.Length > 0;
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            return false;
        }
    }

    private void OnHelpChanged(object sender, RoutedEventArgs e) => FitForm();

    private async Task EnsureBrowserAsync()
    {
        if (Browser.CoreWebView2 is not null)
            return;

        _browserReady ??= InitializeBrowserAsync();
        try
        {
            await _browserReady;
        }
        catch
        {
            _browserReady = null;
            throw;
        }
    }

    private async Task InitializeBrowserAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: AppPaths.Create().WebViewFolder);
        await Browser.EnsureCoreWebView2Async(environment);
        Browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
        Browser.CoreWebView2.DOMContentLoaded += OnDomReady;
    }

    private async void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!TryReadCode(e.Uri, out var code, out var serverDomain))
            return;

        // Код живёт 30 секунд и лежит в адресе перехода. Останавливаем загрузку, чтобы он не ушёл на чужой обработчик.
        e.Cancel = true;
        if (_exchangeStarted)
            return;

        _exchangeStarted = true;
        try
        {
            await _complete(_portal, _clientId, _clientSecret, code, serverDomain, _cancellation.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            FileLog.Write(exception);
            _exchangeStarted = false;
            if (IsLoaded)
                ShowForm(exception.Message);
        }
    }

    private async void OnDomReady(object? sender, CoreWebView2DOMContentLoadedEventArgs e)
    {
        if (_exchangeStarted || Browser.CoreWebView2 is null)
            return;
        if (string.IsNullOrEmpty(_login) && string.IsNullOrEmpty(_password))
            return;

        var uri = Browser.CoreWebView2.Source;
        if (string.Equals(uri, _filledUri, StringComparison.Ordinal))
            return;

        _filledUri = uri;
        try
        {
            await Browser.CoreWebView2.ExecuteScriptAsync(FillScript());
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }
    }

    private string FillScript()
    {
        var login = JsonSerializer.Serialize(_login);
        var password = JsonSerializer.Serialize(_password);
        return
            "(function(){" +
            "var login=" + login + ";" +
            "var password=" + password + ";" +
            "var user=document.querySelector('input[name=\"USER_LOGIN\"]');" +
            "var pass=document.querySelector('input[name=\"USER_PASSWORD\"]');" +
            "if(!user&&!pass)return;" +
            "if(user&&login&&!user.value){user.value=login;user.dispatchEvent(new Event('input',{bubbles:true}));user.dispatchEvent(new Event('change',{bubbles:true}));}" +
            "if(pass&&password&&!pass.value){pass.value=password;pass.dispatchEvent(new Event('input',{bubbles:true}));pass.dispatchEvent(new Event('change',{bubbles:true}));}" +
            "if(user&&pass&&user.value&&pass.value){" +
            "var submit=(pass.form&&pass.form.querySelector('[type=\"submit\"]'))||document.querySelector('input[name=\"Login\"],button[type=\"submit\"]');" +
            "if(submit)submit.click();}" +
            "})();";
    }

    private bool TryReadCode(string rawUri, out string code, out string? serverDomain)
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

        var state = query.TryGetValue("state", out var queryState) ? queryState : fragment.GetValueOrDefault("state");
        if (!string.Equals(state, _state, StringComparison.Ordinal))
            return false;

        if (query.TryGetValue("server_domain", out var domain) || fragment.TryGetValue("server_domain", out domain))
            serverDomain = domain;
        return !string.IsNullOrWhiteSpace(code);
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

    private void OnBackClick(object sender, RoutedEventArgs e) => ShowForm(null);

    private void ShowBrowser()
    {
        SizeToContent = SizeToContent.Manual;
        Width = 960;
        Height = 720;
        FormPanel.Visibility = Visibility.Collapsed;
        BrowserPanel.Visibility = Visibility.Visible;
        WindowFit.Apply(this);
    }

    private void ShowForm(string? error)
    {
        BrowserPanel.Visibility = Visibility.Collapsed;
        FormPanel.Visibility = Visibility.Visible;
        SizeToContent = SizeToContent.Height;
        Width = 480;
        ShowError(error);
        WindowFit.Apply(this);
    }

    private void ShowError(string? error)
    {
        var hasError = !string.IsNullOrWhiteSpace(error);
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        FitForm();
    }

    private void FitForm()
    {
        if (!IsLoaded || FormPanel.Visibility != Visibility.Visible || SizeToContent != SizeToContent.Height)
            return;

        SizeToContent = SizeToContent.Manual;
        SizeToContent = SizeToContent.Height;
        WindowFit.Apply(this);
    }
}
