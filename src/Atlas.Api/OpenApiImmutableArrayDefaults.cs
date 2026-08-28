using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Vev.Atlas.Api;

/// <summary>
/// Works around a System.Text.Json / ASP.NET Core OpenAPI schema-generation limitation that otherwise
/// makes <c>GET /openapi/v1.json</c> return HTTP 500.
///
/// <para>
/// The public Atlas contracts (<c>Vev.Atlas.Contracts</c>) model their collections as
/// <see cref="ImmutableArray{T}"/> optional constructor parameters with a <c>= default</c> default
/// (e.g. <c>ImportAsset.Tags</c>, reached through the <c>POST /api/v1/import</c> request body). When
/// <c>Microsoft.AspNetCore.OpenApi</c> generates the request-body schema it drives
/// <c>System.Text.Json.Schema.JsonSchemaExporter</c>, which serializes each optional parameter's
/// default value to emit the schema <c>default</c> keyword. For a <c>= default</c> value-type
/// parameter STJ records that default as CLR <c>null</c> (and its computed "effective" default is an
/// <em>uninitialized</em> <see cref="ImmutableArray{T}"/> whose <see cref="ImmutableArray{T}.IsDefault"/>
/// is <c>true</c>). Serializing either blows up: a boxed <c>null</c> cannot be unboxed to the
/// non-nullable struct, and an uninitialized array throws the moment it is enumerated —
/// <c>JsonException: The JSON value could not be converted to ImmutableArray&lt;Tag&gt;</c>.
/// </para>
///
/// <para>
/// A schema/document transformer cannot fix this — the failure happens while the schema is being
/// <em>created</em>, before any transformer runs. The only interception point is the
/// <see cref="JsonSerializerOptions"/> that <c>AddOpenApi</c> reads (the app's
/// <c>ConfigureHttpJsonOptions</c> options). This modifier substitutes an <em>initialized</em>
/// <c>ImmutableArray&lt;T&gt;.Empty</c> for the offending parameter defaults so the exporter emits
/// <c>"default": []</c>. <see cref="JsonParameterInfo.HasDefaultValue"/> is left <c>true</c>, so the
/// property stays optional (it is not promoted into the schema's <c>required</c> set), and the
/// parameter-to-property binding is untouched, so request deserialization is unaffected — an omitted
/// array still lands as the contract's empty default.
/// </para>
///
/// <para>
/// The substitution has to poke the internal <c>JsonParameterInfo.DefaultValue</c> backing field
/// because STJ exposes no public setter for it. The reflection is defensive: if a future STJ reshapes
/// that member the modifier degrades to a no-op rather than throwing, and the
/// <c>GET /openapi/v1.json</c> integration test (Api.Tests) would fail loudly rather than the endpoint
/// silently regressing in production.
/// </para>
/// </summary>
internal static class OpenApiImmutableArrayDefaults
{
    private static readonly PropertyInfo? AssociatedParameterProperty =
        typeof(JsonPropertyInfo).GetProperty("AssociatedParameter", BindingFlags.Instance | BindingFlags.Public);

    /// <summary>
    /// A <see cref="DefaultJsonTypeInfoResolver"/> modifier that normalizes optional
    /// <see cref="ImmutableArray{T}"/> constructor-parameter defaults so OpenAPI schema export
    /// succeeds. Safe to run over every object type; it acts only on the specific offending shape.
    /// </summary>
    public static void NormalizeOptionalImmutableArrayDefaults(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || AssociatedParameterProperty is null)
        {
            return;
        }

        foreach (var property in typeInfo.Properties)
        {
            var propertyType = property.PropertyType;
            if (!propertyType.IsGenericType ||
                propertyType.GetGenericTypeDefinition() != typeof(ImmutableArray<>))
            {
                continue;
            }

            var parameter = AssociatedParameterProperty.GetValue(property);
            if (parameter is null)
            {
                continue;
            }

            var parameterType = parameter.GetType();
            if (parameterType.GetProperty("HasDefaultValue")?.GetValue(parameter) is not true)
            {
                continue;
            }

            // Only the broken case — a null recorded default that the exporter cannot serialize.
            if (parameterType.GetProperty("DefaultValue")?.GetValue(parameter) is not null)
            {
                continue;
            }

            var emptyArray = propertyType
                .GetField("Empty", BindingFlags.Static | BindingFlags.Public)?
                .GetValue(null);
            if (emptyArray is null)
            {
                continue;
            }

            var backingField = FindDefaultValueBackingField(parameterType);
            backingField?.SetValue(parameter, emptyArray);
        }
    }

    private static FieldInfo? FindDefaultValueBackingField(Type parameterType)
    {
        for (var type = parameterType; type is not null; type = type.BaseType)
        {
            var field = type.GetField(
                "<DefaultValue>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }
}
