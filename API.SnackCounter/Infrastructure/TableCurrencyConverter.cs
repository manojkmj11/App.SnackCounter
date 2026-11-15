using SnackCounter.Application;
using SnackCounter.Domain;

namespace SnackCounter.Infrastructure;

public sealed class TableCurrencyConverter : ICurrencyConverter
{
    private readonly IReadOnlyDictionary<(Currency From, Currency To), decimal> _rates;

    public TableCurrencyConverter(IReadOnlyDictionary<(Currency From, Currency To), decimal> rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        if (rates.Any(pair => pair.Value <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(rates), "Exchange rates must be positive.");
        }

        _rates = new Dictionary<(Currency From, Currency To), decimal>(rates);
    }

    public decimal Convert(decimal amount, Currency from, Currency to, DateOnly date)
    {
        if (from == to) return amount;
        if (!_rates.TryGetValue((from, to), out var rate))
        {
            throw new InvalidOperationException($"No exchange rate is configured for {from} to {to} on {date}.");
        }

        return decimal.Round(amount * rate, 2, MidpointRounding.ToEven);
    }
}
