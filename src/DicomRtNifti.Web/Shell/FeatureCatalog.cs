using System.Reflection;
using Microsoft.AspNetCore.Components;

namespace DicomRtNifti.Web.Shell;

/// <summary>A discovered feature with the route read from its page's <c>@page</c> directive.</summary>
public sealed record FeatureDescriptor(IWebFeature Feature, string Route)
{
    public string Title => Feature.Title;

    public NavSection Section => Feature.Section;

    public Type Page => Feature.Page;

    /// <summary>The menu link, relative to the app's base href ("" for the root).</summary>
    public string Href => Route.TrimStart('/');
}

/// <summary>
/// The features of the app in menu order. Discovery fails the startup, rather than producing a
/// menu with a hole in it, when a feature cannot be built or two features collide.
/// </summary>
public sealed class FeatureCatalog
{
    private readonly Dictionary<Type, FeatureDescriptor> _byFeatureType;

    private FeatureCatalog(IReadOnlyList<FeatureDescriptor> all)
    {
        All = all;
        // Discovery makes one instance per class; a hand-built catalog may repeat a class, and
        // For() then answers with the first.
        _byFeatureType = new Dictionary<Type, FeatureDescriptor>();
        foreach (var descriptor in all)
            _byFeatureType.TryAdd(descriptor.Feature.GetType(), descriptor);
    }

    /// <summary>Every feature, ordered by section, then order, then title.</summary>
    public IReadOnlyList<FeatureDescriptor> All { get; }

    /// <summary>The descriptor of the feature class <paramref name="featureType"/>.</summary>
    public FeatureDescriptor For(Type featureType) =>
        _byFeatureType.TryGetValue(featureType, out var descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"{featureType.Name} is not a registered feature.");

    /// <summary>Finds every <see cref="IWebFeature"/> class in <paramref name="assembly"/>.</summary>
    public static FeatureCatalog Discover(Assembly assembly)
    {
        var features = new List<IWebFeature>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || !typeof(IWebFeature).IsAssignableFrom(type))
                continue;
            if (type.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidOperationException($"Feature {type.FullName} needs a public parameterless constructor.");
            features.Add((IWebFeature)Activator.CreateInstance(type)!);
        }

        return From(features);
    }

    /// <summary>Validates and orders <paramref name="features"/>.</summary>
    public static FeatureCatalog From(IEnumerable<IWebFeature> features)
    {
        var descriptors = new List<FeatureDescriptor>();
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var feature in features)
        {
            string name = feature.GetType().Name;
            if (string.IsNullOrWhiteSpace(feature.Title))
                throw new InvalidOperationException($"Feature {name} has no title.");
            if (feature.Page is null || !typeof(IComponent).IsAssignableFrom(feature.Page))
                throw new InvalidOperationException($"Feature {name}: Page must be a Razor component.");
            if (feature.Cli is null)
                throw new InvalidOperationException($"Feature {name} declares no CLI equivalent.");

            var pageRoutes = feature.Page.GetCustomAttributes<RouteAttribute>().Select(r => r.Template).ToList();
            if (pageRoutes.Count != 1)
                throw new InvalidOperationException($"Feature {name}: {feature.Page.Name} must have exactly one @page route.");
            string route = pageRoutes[0];
            if (!route.StartsWith('/') || route.Contains('{'))
                throw new InvalidOperationException($"Feature {name}: the route must be a fixed path starting with '/'.");

            if (!titles.Add(feature.Title))
                throw new InvalidOperationException($"Two features are titled '{feature.Title}'.");
            if (!routes.Add(route))
                throw new InvalidOperationException($"Two features use the route '{route}'.");
            descriptors.Add(new FeatureDescriptor(feature, route));
        }

        var ordered = descriptors
            .OrderBy(d => d.Section)
            .ThenBy(d => d.Feature.Order)
            .ThenBy(d => d.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new FeatureCatalog(ordered);
    }
}
