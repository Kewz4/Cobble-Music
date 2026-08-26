namespace CobbleMusicUpdater;

internal static class VersionPolicy
{
    public static bool TryParseCanonical(string? value, out Version? version)
    {
        version = null;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        string[] components = value.Split('.', StringSplitOptions.None);
        if (components.Length != 3)
        {
            return false;
        }
        var numbers = new int[3];
        for (int index = 0; index < components.Length; index++)
        {
            string component = components[index];
            if (component.Length == 0
                || (component.Length > 1 && component[0] == '0')
                || component.Any(character => !char.IsAsciiDigit(character))
                || !int.TryParse(component, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[index]))
            {
                return false;
            }
        }
        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }
}
