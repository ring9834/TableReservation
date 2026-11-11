using System.Globalization;
using System.Reflection;
using FluentValidation;
using FluentValidation.Validators;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Reservation.Api.Pipeline;

/// <summary>
/// ADR-004 "How it is used" 4: copies the basic FluentValidation rules of a request type into its OpenAPI schema, so
/// the zod schemas orval generates for the web app check the same limits (Design §18.4). Only rules that a JSON
/// schema can say exactly are copied:
/// <list type="bullet">
/// <item><c>NotEmpty</c> on a string → <c>minLength: 1</c> (and the property is listed as required when the rule has no condition);</item>
/// <item><c>Length</c>, <c>MinimumLength</c>, <c>MaximumLength</c> → <c>minLength</c> / <c>maxLength</c>;</item>
/// <item><c>InclusiveBetween</c>, <c>GreaterThanOrEqualTo</c>, <c>LessThanOrEqualTo</c> on numbers → <c>minimum</c> / <c>maximum</c>;</item>
/// <item><c>EmailAddress</c> → <c>format: email</c>.</item>
/// </list>
/// Length and range rules with a <c>When</c> condition are still copied: they only apply when a value is sent, as in
/// JSON schema. Custom rules (<c>Must</c>, <c>Custom</c>, such as the NZ mobile check) stay server-only and reach the
/// page as field error codes. Query parameters are not changed.
/// </summary>
internal sealed class FluentValidationSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        // Only whole request types (not their property schemas) that have a validator.
        if (context.JsonPropertyInfo is not null || schema.Properties is not { Count: > 0 } properties)
        {
            return Task.CompletedTask;
        }

        using var scope = context.ApplicationServices.CreateScope();
        var validatorType = typeof(IValidator<>).MakeGenericType(context.JsonTypeInfo.Type);
        if (scope.ServiceProvider.GetService(validatorType) is not IValidator validator)
        {
            return Task.CompletedTask;
        }

        var jsonNames = context.JsonTypeInfo.Properties
            .Where(p => p.AttributeProvider is MemberInfo)
            .ToDictionary(p => ((MemberInfo)p.AttributeProvider!).Name, p => p.Name, StringComparer.Ordinal);

        foreach (var member in validator.CreateDescriptor().GetMembersWithValidators())
        {
            if (!jsonNames.TryGetValue(member.Key, out var jsonName)
                || !properties.TryGetValue(jsonName, out var property)
                || property is not OpenApiSchema propertySchema)
            {
                continue; // unknown member, or a $ref (enums) that must not be changed for every user of the schema
            }

            foreach (var (rule, component) in member)
            {
                Apply(schema, jsonName, propertySchema, rule, conditional: component.HasCondition || component.HasAsyncCondition);
            }
        }

        return Task.CompletedTask;
    }

    private static void Apply(OpenApiSchema owner, string name, OpenApiSchema property, IPropertyValidator rule, bool conditional)
    {
        var isString = property.Type is { } type && type.HasFlag(JsonSchemaType.String);
        var isNumber = property.Type is { } numberType
            && (numberType.HasFlag(JsonSchemaType.Integer) || numberType.HasFlag(JsonSchemaType.Number));
        switch (rule)
        {
            case INotEmptyValidator when isString:
                property.MinLength = Math.Max(property.MinLength ?? 0, 1);
                if (!conditional)
                {
                    owner.Required ??= new HashSet<string>(StringComparer.Ordinal);
                    owner.Required.Add(name);
                }

                break;
            case ILengthValidator length when isString:
                if (length.Min > 0)
                {
                    property.MinLength = Math.Max(property.MinLength ?? 0, length.Min);
                }

                if (length.Max > 0)
                {
                    property.MaxLength = property.MaxLength is { } max ? Math.Min(max, length.Max) : length.Max;
                }

                break;
            case IBetweenValidator { Name: "InclusiveBetweenValidator" } between when isNumber:
                property.Minimum = Number(between.From);
                property.Maximum = Number(between.To);
                break;
            case IComparisonValidator { Comparison: Comparison.GreaterThanOrEqual, ValueToCompare: not null } atLeast when isNumber:
                property.Minimum = Number(atLeast.ValueToCompare);
                break;
            case IComparisonValidator { Comparison: Comparison.LessThanOrEqual, ValueToCompare: not null } atMost when isNumber:
                property.Maximum = Number(atMost.ValueToCompare);
                break;
            case IEmailValidator when isString:
                property.Format = "email";
                break;
        }
    }

    private static string Number(object value) => Convert.ToString(value, CultureInfo.InvariantCulture)!;
}
