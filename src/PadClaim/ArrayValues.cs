using System.Collections.Immutable;
using System.Text;

namespace PadClaim;

/// <summary>Value semantics and printed text for the immutable arrays the records hold.</summary>
internal static class ArrayValues
{
    /// <summary>Tells whether two arrays hold equal elements in the same order.</summary>
    /// <param name="left">One array.</param>
    /// <param name="right">The other array.</param>
    /// <returns>True when both were never initialised, or both hold equal elements in the same order.</returns>
    public static bool Same<T>(ImmutableArray<T> left, ImmutableArray<T> right) =>
        left.IsDefault ? right.IsDefault : !right.IsDefault && left.SequenceEqual(right);

    /// <summary>Adds a marker for an uninitialised array, then every element in order, to <paramref name="hash"/>.</summary>
    /// <param name="hash">The hash being built.</param>
    /// <param name="values">The array to add.</param>
    public static void AddTo<T>(ref HashCode hash, ImmutableArray<T> values)
    {
        hash.Add(values.IsDefault);
        if (!values.IsDefault)
        {
            foreach (T value in values)
            {
                hash.Add(value);
            }
        }
    }

    /// <summary>Appends <c>default</c> for an uninitialised array, else the elements in brackets separated by commas.</summary>
    /// <param name="builder">The builder to append to.</param>
    /// <param name="values">The array to print.</param>
    public static void Append<T>(StringBuilder builder, ImmutableArray<T> values)
    {
        if (values.IsDefault)
        {
            builder.Append("default");
            return;
        }

        builder.Append('[').AppendJoin(", ", values).Append(']');
    }
}
