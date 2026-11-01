namespace NeoSkin.Web;

/// <summary>
/// NeoSkin.NET —— Blessing Skin Server 6.0.11_patch 的 .NET 10 / Native AOT 移植。
///
/// <p>参考实现（权威）：<c>blessing-skin-server-Laravel13</c>（PHP 6.0.11_patch）；
/// 已验证的参考移植：<c>NeoSkinSpringBoot</c>（Java 21 版）。</p>
/// </summary>
public static class Program
{
    public static void Main(string[] args) => NeoSkinApp.Create(args).Run();
}
