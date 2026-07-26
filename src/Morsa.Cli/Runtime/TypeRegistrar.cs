using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace Morsa.Cli.Runtime;

/// <summary>Bridges Spectre.Console.Cli to Microsoft dependency injection.</summary>
internal sealed class TypeRegistrar(IServiceCollection services) : ITypeRegistrar
{
    public ITypeResolver Build() => new TypeResolver(services.BuildServiceProvider());

    public void Register(Type service, Type implementation) => services.AddSingleton(service, implementation);

    public void RegisterInstance(Type service, object implementation) => services.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory) => services.AddSingleton(service, _ => factory());

    private sealed class TypeResolver(IServiceProvider provider) : ITypeResolver, IDisposable
    {
        public object? Resolve(Type? type) => type is null ? null : provider.GetService(type);

        public void Dispose()
        {
            if (provider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}

