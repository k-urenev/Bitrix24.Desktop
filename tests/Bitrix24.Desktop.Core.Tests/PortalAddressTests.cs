using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Tests;

public class PortalAddressTests
{
    [Theory]
    [InlineData("corp.example.ru", "https://corp.example.ru/disk/download/?fileId=1", true)]
    [InlineData("corp.example.ru", "http://corp.example.ru/disk/download/", false)]
    [InlineData("corp.example.ru", "https://evil.example/corp.example.ru", false)]
    [InlineData("corp.example.ru", "https://files.corp.example.ru/a", false)]
    public void File_link_must_be_https_on_the_portal_host(string portal, string url, bool allowed)
    {
        Assert.Equal(allowed, PortalAddress.IsPortalHttps(portal, url));
    }
}
