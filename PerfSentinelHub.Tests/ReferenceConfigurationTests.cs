using System.Reflection;
using System.Text.Json;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Tests;

/// <summary>
///     examples/appsettings.reference.json calls itself the inventory. These tests
///     are what make that true: .NET ignores an unrecognised configuration key in
///     silence, so a setting the reference forgets is a setting nobody discovers
///     until they read the source.
/// </summary>
public sealed class ReferenceConfigurationTests
{
    private static JsonElement Reference()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PerfSentinelHub.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, "examples", "appsettings.reference.json");
        // The provider the Hub itself uses skips comments, so the reference is
        // allowed to carry the annotations that are the point of the file.
        var document = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return document.RootElement.GetProperty("Hub");
    }

    [Fact]
    public void Every_setting_the_Hub_accepts_appears_in_the_reference()
    {
        var missing = new List<string>();
        Walk(typeof(HubOptions), Reference(), "Hub", missing);
        Assert.Empty(missing);
    }

    private static void Walk(Type type, JsonElement element, string prefix, List<string> missing)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Only what the configuration binder can write is a setting. A
            // computed property like EngineSubcommand is derived from one.
            if (!property.CanWrite) continue;

            var path = $"{prefix}:{property.Name}";
            if (!element.TryGetProperty(property.Name, out var value))
            {
                missing.Add(path);
                continue;
            }

            var nested = Nested(property.PropertyType);
            if (nested is null) continue;

            // A list of settings is represented by one fully populated entry:
            // the reference shows what a source looks like, not how many.
            if (value.ValueKind == JsonValueKind.Array)
            {
                if (value.GetArrayLength() == 0)
                {
                    missing.Add($"{path} (needs one populated entry)");
                    continue;
                }

                value = value[0];
            }

            Walk(nested, value, path, missing);
        }
    }

    /// <summary>
    ///     The settings type behind a property, or null when the property is a leaf
    ///     the reference only has to name.
    /// </summary>
    private static Type? Nested(Type type)
    {
        if (type.Namespace == typeof(HubOptions).Namespace)
            return type.IsEnum ? null : type;
        if (!type.IsGenericType) return null;

        var arguments = type.GetGenericArguments()
            .Where(argument => argument.Namespace == typeof(HubOptions).Namespace)
            .ToArray();
        if (arguments.Length == 0) return null;

        // A settings type reached through a container shape this walk does not
        // model would otherwise be skipped in silence, in the one test whose job
        // is catching what the reference forgot.
        Assert.True(
            arguments.Length == 1 && type.GetGenericArguments() is [_],
            $"Unmodelled container {type} holds settings; teach Walk how to descend into it.");
        return arguments[0];
    }
}
