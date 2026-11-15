using SnackCounter.Domain;

namespace SnackCounter.Application;

public interface ICurrencyConverter
{
    decimal Convert(decimal amount, Currency from, Currency to, DateOnly date);
}
