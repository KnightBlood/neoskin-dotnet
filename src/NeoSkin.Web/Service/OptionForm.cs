using System.Text;
using NeoSkin.Web.Support;

namespace NeoSkin.Web.Service;

/// <summary>
/// 后台设置表单构建器（<c>App\Services\OptionForm</c> 的移植）。
///
/// <p>输出与源 Twig <c>resources/views/forms/*.twig</c> 同构的 HTML 字符串，
/// 由模板用 <c>th:utext="${forms.*}"</c> 注入；前端依赖这些 class 与
/// <c>hidden[name=option]</c> / <c>submit_&lt;id&gt;</c> 名称。</p>
/// </summary>
public sealed class OptionForm
{
    public sealed record Context(Option Option, I18n.Translator Translator, string Locale,
        string CsrfField);

    private sealed record Button(string Text, List<string> Css, string Type, string Name,
        string Href);

    private sealed record Message(string Content, string Type);

    private readonly Context _context;
    private readonly string _id;
    private readonly string _title;
    private string? _hint;
    private string _type = "primary";
    private readonly List<Item> _items = [];
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly List<Button> _buttons = [];
    private readonly List<Message> _messages = [];
    private readonly List<Message> _alerts = [];
    private Action<OptionForm>? _always;
    private bool _renderWithoutTable;
    private bool _renderInputTagsOnly;
    private bool _renderWithoutSubmitButton;

    private OptionForm(Context context, string id, string? title)
    {
        _context = context;
        _id = id;
        _title = title ?? Trans("options." + id + ".title", null);
    }

    public static OptionForm Create(Context context, string id) => new(context, id, null);

    public static OptionForm Create(Context context, string id, string title) =>
        new(context, id, title);

    public OptionForm Type(string type)
    {
        _type = type;
        return this;
    }

    public OptionForm Hint(string? hintContent = null)
    {
        _hint = hintContent ?? Trans("options." + _id + ".hint", null);
        return this;
    }

    public TextItem Text(string itemId) => Add(new TextItem(this, itemId, null));

    public TextItem Text(string itemId, string name) => Add(new TextItem(this, itemId, name));

    public CheckboxItem Checkbox(string itemId) => Add(new CheckboxItem(this, itemId, null));

    public CheckboxItem Checkbox(string itemId, string name) =>
        Add(new CheckboxItem(this, itemId, name));

    public TextareaItem Textarea(string itemId) => Add(new TextareaItem(this, itemId, null));

    public TextareaItem Textarea(string itemId, string name) =>
        Add(new TextareaItem(this, itemId, name));

    public SelectItem Select(string itemId) => Add(new SelectItem(this, itemId, null));

    public SelectItem Select(string itemId, string name) =>
        Add(new SelectItem(this, itemId, name));

    public GroupItem Group(string itemId) => Add(new GroupItem(this, itemId, null));

    public GroupItem Group(string itemId, string name) => Add(new GroupItem(this, itemId, name));

    private T Add<T>(T item) where T : Item
    {
        _items.Add(item);
        return item;
    }

    public OptionForm AddButton(string text, string style, string buttonType, string name,
        string href = "")
    {
        _buttons.Add(new Button(text, ["btn", "btn-" + style], buttonType, name, href));
        return this;
    }

    public OptionForm AddMessage(string message, string style)
    {
        _messages.Add(new Message(message, style));
        return this;
    }

    public OptionForm AddAlert(string message, string style)
    {
        _alerts.Add(new Message(message, style));
        return this;
    }

    public OptionForm AddMessageAuto()
    {
        AddMessage(Trans("options." + _id + ".message", null), "info");
        return this;
    }

    public OptionForm With(string key, object? value)
    {
        _values[key] = value;
        return this;
    }

    public OptionForm Always(Action<OptionForm> action)
    {
        _always = action;
        return this;
    }

    public OptionForm RenderWithoutTable()
    {
        _renderWithoutTable = true;
        return this;
    }

    public OptionForm RenderInputTagsOnly()
    {
        _renderInputTagsOnly = true;
        return this;
    }

    public OptionForm RenderWithoutSubmitButton()
    {
        _renderWithoutSubmitButton = true;
        return this;
    }

    // ================================================================== 渲染

    public string Render()
    {
        _always?.Invoke(this);
        if (!_renderWithoutSubmitButton)
        {
            AddButton(Trans("general.submit", null) ?? "提交", "primary", "submit",
                "submit_" + _id);
        }

        AssignValues();

        var html = new StringBuilder(4096);
        html.Append("<div class=\"card card-").Append(_type).Append("\">\n");
        html.Append("  <div class=\"card-header\">\n");
        html.Append("    <h3 class=\"card-title\">").Append(_title).Append(' ')
            .Append(_hint ?? "").Append("</h3>\n");
        html.Append("  </div>\n");
        html.Append("  <form method=\"post\">\n");
        html.Append("    ").Append(_context.CsrfField).Append('\n');
        html.Append("    <input type=\"hidden\" name=\"option\" value=\"").Append(Escape(_id))
            .Append("\">\n");
        html.Append("    <div class=\"card-body\">\n");

        foreach (Message message in _messages)
        {
            html.Append("      <div class=\"callout callout-").Append(message.Type)
                .Append("\">\n        ").Append(message.Content).Append("\n      </div>\n");
        }

        foreach (Message alert in _alerts)
        {
            html.Append("      <div class=\"alert alert-").Append(alert.Type).Append("\">\n");
            html.Append("        ").Append(IconFor(alert.Type)).Append('\n');
            html.Append("        <span>").Append(alert.Content).Append("</span>\n");
            html.Append("      </div>\n");
        }

        if (_renderWithoutTable)
        {
            foreach (Item item in _items)
            {
                html.Append(item.Render());
                if (item.Description is not null)
                {
                    html.Append("        <p class=\"description\">")
                        .Append(item.Description).Append("</p>\n");
                }
            }
        }
        else
        {
            html.Append("      <div class=\"container-fluid\">\n");
            for (int i = 0; i < _items.Count; i++)
            {
                Item item = _items[i];
                string rowClass = i == 0 ? "mb-3"
                    : (i == _items.Count - 1 ? "mt-3" : "my-3");
                html.Append("        <div class=\"row ").Append(rowClass).Append("\">\n");
                if (!_renderInputTagsOnly)
                {
                    html.Append("          <div class=\"col-sm-4\">\n            ")
                        .Append(item.DisplayName()).Append(' ')
                        .Append(item.Hint ?? "").Append('\n');
                    html.Append("          </div>\n");
                }

                html.Append("          <div class=\"col-sm-")
                    .Append(_renderInputTagsOnly ? "12" : "8").Append("\">\n");
                html.Append(item.Render());
                if (item.Description is not null)
                {
                    html.Append("            <p class=\"description\">")
                        .Append(item.Description).Append("</p>\n");
                }

                html.Append("          </div>\n");
                html.Append("        </div>\n");
            }

            html.Append("      </div>\n");
        }

        html.Append("    </div>\n");
        html.Append("    <div class=\"card-footer\">\n");
        foreach (Button button in _buttons)
        {
            string css = string.Join(" ", button.Css);
            if (!string.IsNullOrEmpty(button.Href))
            {
                html.Append("      <a href=\"").Append(Escape(button.Href))
                    .Append("\" class=\"").Append(css).Append("\">\n        ")
                    .Append(button.Text).Append("\n      </a>\n");
            }
            else
            {
                html.Append("      <button type=\"").Append(button.Type).Append("\" name=\"")
                    .Append(Escape(button.Name)).Append("\" class=\"").Append(css)
                    .Append("\">\n        ").Append(button.Text).Append("\n      </button>\n");
            }
        }

        html.Append("    </div>\n");
        html.Append("  </form>\n");
        html.Append("</div>");

        return html.ToString();
    }

    private void AssignValues()
    {
        foreach (Item item in _items)
        {
            if (item is GroupItem group)
            {
                foreach (GroupItem.Inner inner in group.Inners)
                {
                    if (inner.Id is not null && inner.Value is null)
                    {
                        inner.SetValue(ValueById(inner.Id));
                    }
                }

                continue;
            }

            item.Value ??= ValueById(item.Id);
        }
    }

    private object? ValueById(string optionId) =>
        _values.TryGetValue(optionId, out object? value)
            ? value
            : _context.Option.GetLocalized(optionId);

    private static string IconFor(string style) => style switch
    {
        "success" => "<i class=\"fas fa-check icon\"></i>",
        "info" => "<i class=\"fas fa-info icon\"></i>",
        "warning" => "<i class=\"fas fa-exclamation-triangle icon\"></i>",
        "danger" => "<i class=\"fas fa-times-circle icon\"></i>",
        _ => "",
    };

    // ================================================================== 翻译

    private string Trans(string key, string? fallback)
    {
        object? value = _context.Translator.Get(key, null, _context.Locale);
        if (value is string text && text != key)
        {
            return text;
        }

        return fallback ?? key;
    }

    private string? OptionText(string itemId, string suffix)
    {
        string key = "options." + _id + "." + itemId + "." + suffix;
        object? value = _context.Translator.Get(key, null, _context.Locale);
        return value is string text && text != key ? text : null;
    }

    private string AutoName(string itemId)
    {
        string key = "options." + _id + "." + itemId;
        object? node = _context.Translator.Get(key, null, _context.Locale);
        object? title = node switch
        {
            PhpArray array => array.Get("title"),
            IReadOnlyDictionary<string, object?> dictionary
                when dictionary.TryGetValue("title", out object? found) => found,
            _ => null,
        };
        if (title is not null)
        {
            return title.ToString() ?? key;
        }

        return node is string text && text != key ? text : key;
    }

    internal static string Escape(string? value) =>
        (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&#039;");

    // ================================================================== 条目

    public abstract class Item
    {
        protected Item(OptionForm form, string id, string? explicitName)
        {
            Form = form;
            Id = id;
            ExplicitName = explicitName;
        }

        internal OptionForm Form { get; }

        public string Id { get; }

        private string? ExplicitName { get; }

        public string? Hint { get; private set; }

        public string? Description { get; private set; }

        public object? Value { get; set; }

        public string? Disabled { get; private set; }

        public abstract string Render();

        public string DisplayName() => ExplicitName ?? Form.AutoName(Id);

        public Item WithHint(string? content = null)
        {
            Hint = content ?? Form.OptionText(Id, "hint");
            return this;
        }

        public Item Describe(string? text = null)
        {
            string? content = text ?? Form.OptionText(Id, "description");
            Description = content is null
                ? null
                : "<i class=\"fas fa-question-circle\" title=\"" + Escape(content)
                  + "\" data-toggle=\"tooltip\" data-placement=\"top\"></i>";
            return this;
        }

        public Item Disable()
        {
            Disabled = "disabled";
            return this;
        }
    }

    public sealed class TextItem : Item
    {
        private string _placeholder = "";

        internal TextItem(OptionForm form, string id, string? explicitName)
            : base(form, id, explicitName)
        {
        }

        public TextItem Placeholder(string? text = null)
        {
            _placeholder = text ?? Form.OptionText(Id, "placeholder") ?? "";
            return this;
        }

        public override string Render() =>
            "<input\n              type=\"text\"\n              class=\"form-control\"\n"
            + "              name=\"" + Escape(Id) + "\"\n              "
            + (Disabled is not null ? "disabled=disabled" : "") + "\n"
            + "              value=\"" + Escape(Value?.ToString() ?? "") + "\"\n"
            + "              placeholder=\"" + Escape(_placeholder) + "\"\n            >\n";
    }

    public sealed class CheckboxItem : Item
    {
        private string? _label;

        internal CheckboxItem(OptionForm form, string id, string? explicitName)
            : base(form, id, explicitName)
        {
        }

        public CheckboxItem Label(string? text = null)
        {
            _label = text ?? Form.OptionText(Id, "label");
            return this;
        }

        public override string Render()
        {
            bool isChecked = Value is bool flag ? flag
                : Value is not null && Value.ToString() is not ("0" or "")
                  && !string.Equals(Value.ToString(), "false",
                      StringComparison.OrdinalIgnoreCase);
            return "<label for=\"" + Escape(Id) + "\">\n"
                + "              <input\n                type=\"checkbox\"\n                "
                + (isChecked ? "checked=\"true\"" : "") + "\n                id=\""
                + Escape(Id) + "\"\n                name=\"" + Escape(Id) + "\"\n"
                + "                " + (Disabled is not null ? "disabled=disabled" : "") + "\n"
                + "                value=\"true\"\n              > "
                + Escape(_label ?? "") + "\n            </label>\n";
        }
    }

    public sealed class TextareaItem : Item
    {
        private int _rows = 3;

        internal TextareaItem(OptionForm form, string id, string? explicitName)
            : base(form, id, explicitName)
        {
        }

        public TextareaItem Rows(int rows)
        {
            _rows = rows;
            return this;
        }

        public override string Render() =>
            "<textarea\n              class=\"form-control\"\n              rows=\"" + _rows
            + "\"\n              name=\"" + Escape(Id) + "\"\n              "
            + (Disabled is not null ? "disabled=disabled" : "") + "\n            >"
            + Escape(Value?.ToString() ?? "") + "</textarea>\n";
    }

    public sealed class SelectItem : Item
    {
        private readonly List<(string Value, string Name)> _options = [];

        internal SelectItem(OptionForm form, string id, string? explicitName)
            : base(form, id, explicitName)
        {
        }

        public SelectItem Option(string optionValue, string name)
        {
            _options.Add((optionValue, name));
            return this;
        }

        public override string Render()
        {
            var html = new StringBuilder();
            html.Append("<select class=\"form-control\" name=\"").Append(Escape(Id)).Append("\" ")
                .Append(Disabled is not null ? "disabled=disabled" : "").Append(">\n");
            foreach ((string value, string name) in _options)
            {
                bool selected = Value is not null && Value.ToString() == value;
                html.Append("              <option\n                value=\"")
                    .Append(Escape(value)).Append("\"\n                ")
                    .Append(selected ? "selected=selected" : "").Append("\n              >\n")
                    .Append("                ").Append(Escape(name))
                    .Append("\n              </option>\n");
            }

            html.Append("            </select>\n");
            return html.ToString();
        }
    }

    public sealed class GroupItem : Item
    {
        public sealed class Inner(string type, string? id, object? value)
        {
            public string Type { get; } = type;

            public string? Id { get; } = id;

            public object? Value { get; private set; } = value;

            public void SetValue(object? value) => Value = value;
        }

        internal GroupItem(OptionForm form, string id, string? explicitName)
            : base(form, id, explicitName)
        {
        }

        public List<Inner> Inners { get; } = [];

        public GroupItem Text(string innerId)
        {
            Inners.Add(new Inner("text", innerId, null));
            return this;
        }

        public GroupItem Addon(string? text = null)
        {
            string value = text ?? Form.OptionText(Id, "addon") ?? "";
            Inners.Add(new Inner("addon", null, value));
            return this;
        }

        public override string Render()
        {
            var html = new StringBuilder();
            html.Append("<div class=\"input-group\">\n");
            foreach (Inner inner in Inners)
            {
                if (inner.Type == "addon")
                {
                    html.Append("              <div class=\"input-group-append\">\n")
                        .Append("                <span class=\"input-group-text\">")
                        .Append(Escape(inner.Value?.ToString()))
                        .Append("</span>\n              </div>\n");
                }
                else
                {
                    var text = new TextItem(Form, inner.Id!, null) { Value = inner.Value };
                    html.Append(text.Render());
                }
            }

            html.Append("            </div>\n");
            return html.ToString();
        }
    }
}
