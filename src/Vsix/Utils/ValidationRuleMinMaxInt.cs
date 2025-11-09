using System.Globalization;
using System.Windows.Controls;

namespace Disasmo.Utils;

public class ValidationRuleMinMaxInt : ValidationRuleStringAsInt
{		
	public int Min { get; set; }
	public int Max { get; set; }

	public override ValidationResult Validate(object value, CultureInfo cultureInfo)
	{
		var result = ValidateInternal(value, cultureInfo, out int parsedValue);

		if (result.IsValid) 
		{
			if (parsedValue < Min)
				result = new ValidationResult(false, $"Please enter a value greater than {Min}!");
			else if (parsedValue > Max)
				result = new ValidationResult(false, $"Please enter a value less than {Max}!");
		}

		return result;
	}
}