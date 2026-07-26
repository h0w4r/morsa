using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Morsa.Application.Abstractions;
using Morsa.PluginSdk;

namespace Morsa.PluginHost;

/// <summary>
/// Adapts one managed <see cref="IMorsaPlugin"/> assembly to the external morsa-plugin/1
/// JSONL protocol. The outer plugin runner is responsible for OS sandboxing and budgets.
/// </summary>
internal static class ManagedPluginAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string assemblyPath, int maximumLineBytes)
    {
        try
        {
            await using var session = await ManagedPluginSession.LoadAsync(assemblyPath).ConfigureAwait(false);
            var initialized = false;
            string? line;
            while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                ValidateLine(line, maximumLineBytes);
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                var message = document.RootElement;
                var type = message.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
                if (!initialized)
                {
                    ValidateInitialization(message, session.Plugin.Manifest);
                    initialized = true;
                    await WriteAsync(new
                    {
                        type = "initialized",
                        protocol = "morsa-plugin/1",
                        plugin_id = session.Plugin.Manifest.Id,
                    }).ConfigureAwait(false);
                    continue;
                }

                if (type != "request")
                {
                    throw new InvalidDataException("Managed plugin messages after initialization must be requests.");
                }

                var requestId = message.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
                var operation = message.TryGetProperty("operation", out var operationNode) ? operationNode.GetString() : null;
                switch (operation)
                {
                    case "manifest":
                        await WriteAsync(new
                        {
                            type = "result",
                            id = requestId,
                            manifest = session.Plugin.Manifest,
                        }).ConfigureAwait(false);
                        break;
                    case "capabilities":
                        await WriteAsync(new
                        {
                            type = "result",
                            id = requestId,
                            capabilities = session.Registry.CreateSnapshot(),
                        }).ConfigureAwait(false);
                        break;
                    default:
                        await WriteAsync(new
                        {
                            type = "error",
                            id = requestId,
                            code = "UNSUPPORTED_OPERATION",
                            message = $"Managed plugin operation '{operation}' is not supported by morsa-plugin/1.",
                        }).ConfigureAwait(false);
                        break;
                }
            }

            return initialized ? 0 : 8;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or ReflectionTypeLoadException or TypeLoadException)
        {
            // stderr is captured and redacted by PluginProcessRunner; stdout remains JSONL-only.
            Console.Error.WriteLine(exception.Message);
            return 8;
        }
    }

    private static void ValidateInitialization(JsonElement message, PluginManifest manifest)
    {
        if (!message.TryGetProperty("type", out var type) || type.GetString() != "initialize" ||
            !message.TryGetProperty("protocol", out var protocol) || protocol.GetString() != "morsa-plugin/1")
        {
            throw new InvalidDataException("The first managed plugin message must initialize morsa-plugin/1.");
        }

        var requestedId = message.TryGetProperty("plugin_id", out var id) ? id.GetString() : null;
        if (!string.Equals(requestedId, manifest.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Managed plugin identity does not match the installed manifest.");
        }

        var granted = message.TryGetProperty("permissions", out var permissions) && permissions.ValueKind == JsonValueKind.Array
            ? permissions.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToHashSet(StringComparer.Ordinal)
            : [];
        if (!granted.SetEquals(manifest.Permissions))
        {
            throw new InvalidDataException("Managed plugin permissions do not match the installed manifest.");
        }
    }

    private static void ValidateLine(string line, int maximumLineBytes)
    {
        if (Encoding.UTF8.GetByteCount(line) > maximumLineBytes)
        {
            throw new InvalidDataException("Managed plugin JSONL line exceeds 4 MiB.");
        }
    }

    private static async Task WriteAsync<T>(T message)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(message, JsonOptions)).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }
}

/// <summary>Owns the collectible load context and the capability registry for one plugin.</summary>
internal sealed class ManagedPluginSession : IAsyncDisposable
{
    private ManagedPluginSession(ManagedPluginLoadContext loadContext, IMorsaPlugin plugin, ManagedPluginRegistry registry)
    {
        LoadContext = loadContext;
        Plugin = plugin;
        Registry = registry;
    }

    private ManagedPluginLoadContext LoadContext { get; }

    public IMorsaPlugin Plugin { get; }

    public ManagedPluginRegistry Registry { get; }

    public static async Task<ManagedPluginSession> LoadAsync(string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath) || !Path.GetExtension(fullPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Managed plugin entry point must be an existing .NET assembly.");
        }

        var context = new ManagedPluginLoadContext(fullPath);
        try
        {
            var assembly = context.LoadFromAssemblyPath(fullPath);
            var pluginTypes = GetLoadableTypes(assembly)
                .Where(type => typeof(IMorsaPlugin).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false })
                .ToArray();
            if (pluginTypes.Length != 1)
            {
                throw new InvalidDataException("Managed plugin assembly must expose exactly one concrete IMorsaPlugin implementation.");
            }

            var plugin = Activator.CreateInstance(pluginTypes[0]) as IMorsaPlugin
                ?? throw new InvalidDataException("Managed plugin must have an accessible parameterless constructor.");
            ValidateManifest(plugin.Manifest);
            var registry = new ManagedPluginRegistry();
            await plugin.RegisterAsync(registry, CancellationToken.None).ConfigureAwait(false);
            return new ManagedPluginSession(context, plugin, registry);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        Registry.Clear();
        LoadContext.Unload();
        return ValueTask.CompletedTask;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            throw new InvalidDataException(
                $"Managed plugin types could not be loaded: {string.Join("; ", exception.LoaderExceptions.Where(item => item is not null).Select(item => item!.Message))}",
                exception);
        }
    }

    private static void ValidateManifest(PluginManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name) ||
            string.IsNullOrWhiteSpace(manifest.Version) || manifest.ApiVersion != "1")
        {
            throw new InvalidDataException("Managed plugin manifest is incomplete or uses an unsupported API version.");
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "network", "filesystem:read", "filesystem:write", "secrets", "process",
        };
        if (manifest.Permissions.Any(permission => !allowed.Contains(permission)))
        {
            throw new InvalidDataException("Managed plugin requests an unknown permission.");
        }
    }
}

/// <summary>Loads private plugin dependencies while sharing the stable Morsa contracts.</summary>
internal sealed class ManagedPluginLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Morsa.PluginSdk", "Morsa.Application", "Morsa.Domain",
    };

    private readonly AssemblyDependencyResolver _resolver;

    public ManagedPluginLoadContext(string pluginAssemblyPath)
        : base($"morsa-plugin:{Path.GetFileNameWithoutExtension(pluginAssemblyPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name && SharedAssemblies.Contains(name))
        {
            return Default.Assemblies.FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? 0 : LoadUnmanagedDllFromPath(path);
    }
}

/// <summary>Records only the capabilities exposed by the stable SDK registry.</summary>
internal sealed class ManagedPluginRegistry : IMorsaPluginRegistry
{
    private readonly List<IArtifactExtractor> _extractors = [];
    private readonly List<ISearchProvider> _providers = [];

    public void RegisterExtractor(IArtifactExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        if (_extractors.Any(item => string.Equals(item.Id, extractor.Id, StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"Duplicate managed extractor id '{extractor.Id}'.");
        }

        _extractors.Add(extractor);
    }

    public void RegisterSearchProvider(ISearchProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (_providers.Any(item => string.Equals(item.Id, provider.Id, StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"Duplicate managed search provider id '{provider.Id}'.");
        }

        _providers.Add(provider);
    }

    public object CreateSnapshot() => new
    {
        extractors = _extractors.Select(extractor => new
        {
            id = extractor.Id,
            version = extractor.Version,
            supported_kinds = extractor.SupportedKinds.Select(kind => kind.ToString().ToLowerInvariant()).ToArray(),
        }).ToArray(),
        search_providers = _providers.Select(provider => new { id = provider.Id }).ToArray(),
    };

    public void Clear()
    {
        _extractors.Clear();
        _providers.Clear();
    }
}
