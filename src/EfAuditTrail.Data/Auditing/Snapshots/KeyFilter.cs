using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfAuditTrail.Data.Auditing.Snapshots;

/// <summary>Builds <c>e =&gt; keys.Contains(e.Id)</c> (or an OR of AND-ed equalities for a composite key)
/// over the rows the snapshot locked, so the bulk statement touches exactly those rows.</summary>
internal static class KeyFilter
{
    public static Expression<Func<T, bool>> For<T>(IKey key, IReadOnlyCollection<T> entities)
        where T : class
    {
        if (entities.Count == 0)
        {
            throw new ArgumentException("A key filter needs at least one entity.", nameof(entities));
        }

        var parameter = Expression.Parameter(typeof(T), "e");
        var properties = key.Properties.Select(PropertyOf).ToList();

        if (properties.Count == 1)
        {
            var property = properties[0];
            var values = Array.CreateInstance(property.PropertyType, entities.Count);
            var index = 0;
            foreach (var entity in entities)
            {
                values.SetValue(property.GetValue(entity), index++);
            }

            var contains = Expression.Call(
                typeof(Enumerable),
                nameof(Enumerable.Contains),
                [property.PropertyType],
                Expression.Constant(values),
                Expression.Property(parameter, property));

            return Expression.Lambda<Func<T, bool>>(contains, parameter);
        }

        Expression? body = null;
        foreach (var entity in entities)
        {
            Expression? match = null;
            foreach (var property in properties)
            {
                var equal = Expression.Equal(
                    Expression.Property(parameter, property),
                    Expression.Constant(property.GetValue(entity), property.PropertyType));
                match = match is null ? equal : Expression.AndAlso(match, equal);
            }

            body = body is null ? match : Expression.OrElse(body, match!);
        }

        return Expression.Lambda<Func<T, bool>>(body!, parameter);
    }

    /// <summary>The entity's key as one string, in key-property order, for matching before and after rows.</summary>
    public static string ValueOf<T>(IKey key, T entity)
        where T : class
        => string.Join('|', key.Properties.Select(p => Convert.ToString(PropertyOf(p).GetValue(entity), System.Globalization.CultureInfo.InvariantCulture)));

    private static System.Reflection.PropertyInfo PropertyOf(IProperty property)
        => property.PropertyInfo
           ?? throw new NotSupportedException(
               $"{property.DeclaringType.ClrType.Name}.{property.Name}: snapshot auditing needs CLR key properties, not shadow keys.");
}
