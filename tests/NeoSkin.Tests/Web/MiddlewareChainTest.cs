using NeoSkin.Web.Middleware;

namespace NeoSkin.Tests.Web;

/// <summary><see cref="MiddlewareChain"/> 的组展开与别名改写测试。</summary>
public sealed class MiddlewareChainTest
{
    [Fact]
    public void GlobalStackOrderMatchesBootstrap()
    {
        List<string> expanded = MiddlewareChain.Expand([]);
        Assert.Equal(
        [
            "Illuminate\\Foundation\\Http\\Middleware\\PreventRequestsDuringMaintenance",
            "Illuminate\\Foundation\\Http\\Middleware\\ValidatePostSize",
            "Illuminate\\Foundation\\Http\\Middleware\\TrimStrings",
            "App\\Http\\Middleware\\ConvertEmptyStringsToNull",
            "App\\Http\\Middleware\\DetectLanguagePrefer",
        ], expanded);
    }

    [Fact]
    public void WebGroupExpandsInOrder()
    {
        List<string> expanded = MiddlewareChain.Expand(["web"]);
        Assert.Equal(5 + 9, expanded.Count);
        Assert.Equal("Illuminate\\Cookie\\Middleware\\EncryptCookies", expanded[5]);
        Assert.Equal("Illuminate\\View\\Middleware\\ShareErrorsFromSession", expanded[8]);
        Assert.Equal("Illuminate\\Routing\\Middleware\\SubstituteBindings", expanded[12]);
        Assert.Equal("Laravel\\Passport\\Http\\Middleware\\CreateFreshApiToken", expanded[^1]);
    }

    [Fact]
    public void AuthorizeGroupNestsAuthAlias()
    {
        List<string> expanded = MiddlewareChain.Expand(["web", "authorize"]);
        Assert.Equal(5 + 9 + 4, expanded.Count);
        Assert.Equal("App\\Http\\Middleware\\Authenticate:web", expanded[14]);
        Assert.Equal("App\\Http\\Middleware\\RejectBannedUser", expanded[15]);
        Assert.Equal("App\\Http\\Middleware\\EnsureEmailFilled", expanded[16]);
        Assert.Equal("App\\Http\\Middleware\\FireUserAuthenticated", expanded[17]);
    }

    [Fact]
    public void MixedGroupsKeepWrittenOrder()
    {
        List<string> expanded = MiddlewareChain.Expand(["api", "role:admin", "throttle:60,1"]);
        Assert.Equal(5 + 1 + 2, expanded.Count);
        Assert.Equal("Illuminate\\Routing\\Middleware\\SubstituteBindings", expanded[5]);
        Assert.Equal("App\\Http\\Middleware\\CheckRole:admin", expanded[6]);
        Assert.Equal("Illuminate\\Routing\\Middleware\\ThrottleRequests:60,1", expanded[7]);
    }

    [Fact]
    public void CanonicalRewritesAliasesOnly()
    {
        Assert.Equal("App\\Http\\Middleware\\CheckRole:admin", MiddlewareChain.Canonical("role:admin"));
        Assert.Equal("Laravel\\Passport\\Http\\Middleware\\CheckToken", MiddlewareChain.Canonical("scopes"));
        Assert.Equal("App\\Vendor\\Thing", MiddlewareChain.Canonical("App\\Vendor\\Thing"));
        Assert.Equal(["web"], MiddlewareChain.GroupsOf(["web", "auth"]));
    }
}
