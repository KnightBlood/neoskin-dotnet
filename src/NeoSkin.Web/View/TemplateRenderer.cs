namespace NeoSkin.Web.View;

/// <summary>
/// 应用级模板渲染入口：从输出目录的 <c>Resources/views</c> 加载模板
/// （AOT 下不依赖运行时编译）。
/// </summary>
public static class TemplateRenderer
{
    private static readonly Lazy<TemplateEngine> LazyEngine = new(() =>
        new TemplateEngine(new TemplateRepository(new FileTemplateSource(
            Path.Combine(AppContext.BaseDirectory, "Resources", "views")))));

    public static TemplateEngine Engine => LazyEngine.Value;

    public static string Render(string template,
        IReadOnlyDictionary<string, object?> variables) =>
        Engine.Render(template, TemplateContext.Root(template, variables));

    public static string RenderFragment(TemplateFragmentRef fragment,
        IReadOnlyDictionary<string, object?> variables) =>
        Engine.RenderFragment(fragment,
            TemplateContext.Root(fragment.Template, variables));
}
