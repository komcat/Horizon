namespace Horizon.Process.Recipe;

public sealed record RecipeParseError(int Line, int Column, string Message);
