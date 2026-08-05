namespace HubServiceCs;

// App is a hub app: a header plus a UI body of elements (TS app.ts parity).
// Body entries may be plain strings (shorthand text) or element dictionaries
// built with the static helpers below (e.g. App.Text(...), App.Button(...)).
public sealed class App
{
    public AppHeader Header { get; init; } = new();

    public IReadOnlyList<object?> Body { get; init; } = [];

    public object? Top { get; init; }

    public object? Bottom { get; init; }

    public IReadOnlyDictionary<string, object?> Data { get; init; } = new Dictionary<string, object?>();

    // Element helpers produce element dictionaries matching the TS app.ts wire
    // shapes.

    public static Dictionary<string, object?> Text(string value)
    {
        return new Dictionary<string, object?> { ["type"] = "text", ["value"] = value };
    }

    public static Dictionary<string, object?> Progress(string value, object? min = null, object? max = null)
    {
        var element = new Dictionary<string, object?> { ["type"] = "progress", ["value"] = value };
        if (min is not null)
        {
            element["min"] = min;
        }

        if (max is not null)
        {
            element["max"] = max;
        }

        return element;
    }

    public static Dictionary<string, object?> TextField(string value, string? placeholder = null, Action? action = null)
    {
        var element = new Dictionary<string, object?> { ["type"] = "textField", ["value"] = value };
        if (!string.IsNullOrEmpty(placeholder))
        {
            element["placeholder"] = placeholder;
        }

        if (action is not null)
        {
            element["action"] = action;
        }

        return element;
    }

    public static Dictionary<string, object?> Button(string title, Action action)
    {
        return new Dictionary<string, object?> { ["type"] = "button", ["title"] = title, ["action"] = action };
    }

    public static Dictionary<string, object?> Slider(
        string value,
        object? defaultValue = null,
        object? min = null,
        object? max = null,
        object? step = null,
        Action? action = null)
    {
        var element = new Dictionary<string, object?> { ["type"] = "slider", ["value"] = value };
        if (defaultValue is not null)
        {
            element["defaultValue"] = defaultValue;
        }

        if (min is not null)
        {
            element["min"] = min;
        }

        if (max is not null)
        {
            element["max"] = max;
        }

        if (step is not null)
        {
            element["step"] = step;
        }

        if (action is not null)
        {
            element["action"] = action;
        }

        return element;
    }

    public static Dictionary<string, object?> List(string data, object? content)
    {
        return new Dictionary<string, object?> { ["type"] = "list", ["data"] = data, ["content"] = content };
    }

    public static Dictionary<string, object?> Picker(IEnumerable<string> options, string selected)
    {
        return new Dictionary<string, object?> { ["type"] = "picker", ["options"] = options, ["selected"] = selected };
    }

    public static Dictionary<string, object?> Cell(object? title = null, object? subtitle = null)
    {
        var element = new Dictionary<string, object?> { ["type"] = "cell" };
        if (title is not null)
        {
            element["title"] = title;
        }

        if (subtitle is not null)
        {
            element["subtitle"] = subtitle;
        }

        return element;
    }

    public static Dictionary<string, object?> Files(string value, object? title = null, Action? action = null)
    {
        var element = new Dictionary<string, object?> { ["type"] = "files", ["value"] = value };
        if (title is not null)
        {
            element["title"] = title;
        }

        if (action is not null)
        {
            element["action"] = action;
        }

        return element;
    }

    public static Dictionary<string, object?> FileOperation(string? format = null, object? title = null, Action? action = null)
    {
        var element = new Dictionary<string, object?> { ["type"] = "fileOperation" };
        if (!string.IsNullOrEmpty(format))
        {
            element["format"] = format;
        }

        if (title is not null)
        {
            element["title"] = title;
        }

        if (action is not null)
        {
            element["action"] = action;
        }

        return element;
    }

    public static Dictionary<string, object?> HStack(IEnumerable<object?> content, object? spacing = null)
    {
        var element = new Dictionary<string, object?> { ["type"] = "hstack", ["content"] = content };
        if (spacing is not null)
        {
            element["spacing"] = spacing;
        }

        return element;
    }

    public static Dictionary<string, object?> VStack(IEnumerable<object?> content, object? spacing = null)
    {
        var element = new Dictionary<string, object?> { ["type"] = "vstack", ["content"] = content };
        if (spacing is not null)
        {
            element["spacing"] = spacing;
        }

        return element;
    }

    public static Dictionary<string, object?> ZStack(IEnumerable<object?> content)
    {
        return new Dictionary<string, object?> { ["type"] = "zstack", ["content"] = content };
    }

    public static Dictionary<string, object?> Spacer()
    {
        return new Dictionary<string, object?> { ["type"] = "spacer" };
    }
}

// Action references an API call from a UI element.
public sealed class Action
{
    public string Path { get; init; } = "";

    public object? Body { get; init; }

    public object? Output { get; init; }
}
