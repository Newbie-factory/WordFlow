using System.Collections.ObjectModel;

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
            if (!double.IsFinite(this.values[index]))
            {
                throw new ArgumentOutOfRangeException(nameof(values), $"FSRS parameter {index} must be finite.");
            }
        }

        if (this.values[20] <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(values), "The forgetting-curve decay parameter must be positive.");
        }

        Values = new ReadOnlyCollection<double>(this.values);
    }

    public static FsrsParameters Default { get; } = new(OfficialDefaults);

    public IReadOnlyList<double> Values { get; }

    internal double this[int index] => values[index];
}
