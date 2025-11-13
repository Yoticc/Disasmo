using System.Globalization;
using System.Windows.Controls;

namespace Disasmo.Utils;

public class ValidationRuleStringAsInt : ValidationRule
{
	public override ValidationResult Validate(object value, CultureInfo cultureInfo) =>
        ValidateInternal(value, cultureInfo, out _);

    protected ValidationResult ValidateInternal(object value, CultureInfo cultureInfo, out int parsedValue)
	{
		parsedValue = 0;

		if (value is int)
			return ValidationResult.ValidResult;

		if (value is string valueAsString)
		{
			if (int.TryParse(valueAsString, NumberStyles.Integer, cultureInfo, out parsedValue))
                return ValidationResult.ValidResult;
        }

		return new ValidationResult(false, "Please enter a valid number!");
	}
}