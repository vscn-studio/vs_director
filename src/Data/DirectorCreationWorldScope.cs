namespace VSDirector.Data;

public static class DirectorCreationWorldScope
{
    // An unbound legacy project is not implicitly a project of every world.
    public static bool Matches(string projectWorld, int projectDimension, string currentWorld, int currentDimension)
        => !string.IsNullOrWhiteSpace(projectWorld) && !string.IsNullOrWhiteSpace(currentWorld)
            && string.Equals(projectWorld, currentWorld, StringComparison.Ordinal) && projectDimension == currentDimension;
}
