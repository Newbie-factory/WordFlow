using System.Collections.ObjectModel;
using System.Globalization;

namespace WordFlow.Domain.Scheduling;

public sealed class FsrsParameters
{
    private const int ParameterCount = 21;

    private static readonly double[] OfficialDefaults =
    [
        0.212,
        1.2931,
        2.3065,
        8.2956,
        6.4133,
        0.8334,
        3.0194,
        0.001,
        1.8722,
        0.1666,
        0.796,
        1.4835,
        0.0614,
        0.2629,
        1.6483,
        0.6014,
        1.8729,
        0.5425,
        0.0912,
        0.0658,
        0.1542,
    ];

    private static readonly double[] OfficialLowerBounds =
    [
        0.001, 0.001, 0.001, 0.001, 1.0, 0.001, 0.001,
        0.001, 0.0, 0.0, 0.001, 0.001, 0.001, 0.001,
        0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.1,
    ];

    private static readonly double[] OfficialUpperBounds =
    [
        100.0, 100.0, 100.0, 100.0, 10.0, 4.0, 4.0,
        0.75, 4.5, 0.8, 3.5, 5.0, 0.25, 0.9,
        4.0, 1.0, 6.0, 2.0, 2.0, 0.8, 0.8,
    ];

    private readonly double[] values;

    public FsrsParameters(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        this.values = values.ToArray();
        if (this.values.Length != ParameterCount)
        {
            throw new ArgumentException($"FSRS-6 requires exactly {ParameterCount} parameters.", nameof(values));
        }

        for (var index = 0; index < this.values.Length; index++)
        {
            var value = this.values[index];
            var lower = OfficialLowerBounds[index];
            var upper = OfficialUpperBounds[index];
            if (!double.IsFinite(value) || value < lower || value > upper)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(values),
                    value,
                    $"FSRS parameter at index {index} with value "
                    + $"{value.ToString("R", CultureInfo.InvariantCulture)} must be finite and within "
                    + $"[{lower.ToString("R", CultureInfo.InvariantCulture)}, "
                    + $"{upper.ToString("R", CultureInfo.InvariantCulture)}].");
            }
        }

        Values = new ReadOnlyCollection<double>(this.values);
    }

    public static FsrsParameters Default { get; } = new(OfficialDefaults);

    public IReadOnlyList<double> Values { get; }

    internal double this[int index] => values[index];
}
