namespace VSDirector.Data;

public readonly record struct DirectorWorkflowGhostActor(
    string GhostActorKey,
    string SourceActorKey,
    DirectorWorkflowCard? EntityCard);

public static class DirectorWorkflowActorBinding
{
    private const string GhostPrefix = "workflow-ghost:";

    public static string GhostActorKey(Guid entityCardId)
        => entityCardId == Guid.Empty ? string.Empty : GhostPrefix + entityCardId.ToString("N");

    public static string GhostActorKey(string sourceActorKey)
    {
        string source = sourceActorKey?.Trim() ?? string.Empty;
        return source.Length == 0 ? string.Empty : GhostPrefix + "source:" + source;
    }

    public static DirectorWorkflowCard? ResolveEntityCard(
        DirectorWorkflow workflow,
        DirectorWorkflowCard card)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(card);
        IReadOnlyList<DirectorWorkflowCard> entityCards = (workflow.Cards ?? new List<DirectorWorkflowCard>())
            .Where(candidate => candidate is not null
                && candidate.Enabled
                && candidate.Kind == DirectorWorkflowCardKind.Entity)
            .ToArray();
        if (card.Kind == DirectorWorkflowCardKind.Entity)
        {
            return card.Enabled ? card : null;
        }
        if (card.BoundCardId != Guid.Empty)
        {
            DirectorWorkflowCard? bound = entityCards.FirstOrDefault(candidate => candidate.Id == card.BoundCardId);
            if (bound is not null)
            {
                return bound;
            }
        }

        string selector = card.ActorKey?.Trim() ?? string.Empty;
        if (selector.Length > 0)
        {
            DirectorWorkflowCard? matching = entityCards.FirstOrDefault(candidate =>
                string.Equals(candidate.ActorKey, selector, StringComparison.Ordinal));
            if (matching is not null)
            {
                return matching;
            }
        }
        return entityCards.Count == 1 ? entityCards[0] : null;
    }

    public static string ResolveRuntimeActorKey(
        DirectorWorkflow workflow,
        DirectorWorkflowCard card)
    {
        DirectorWorkflowCard? entityCard = ResolveEntityCard(workflow, card);
        if (entityCard is not null)
        {
            return GhostActorKey(entityCard.Id);
        }
        return GhostActorKey(card.ActorKey);
    }

    public static IReadOnlyList<DirectorWorkflowGhostActor> CollectGhostActors(DirectorWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        List<DirectorWorkflowGhostActor> actors = new();
        foreach (DirectorWorkflowCard entityCard in (workflow.Cards ?? new List<DirectorWorkflowCard>())
            .Where(card => card is not null && card.Enabled && card.Kind == DirectorWorkflowCardKind.Entity))
        {
            string source = entityCard.ActorKey?.Trim() ?? string.Empty;
            string ghost = GhostActorKey(entityCard.Id);
            if (source.Length > 0 && ghost.Length > 0)
            {
                actors.Add(new DirectorWorkflowGhostActor(ghost, source, entityCard));
            }
        }

        foreach (DirectorWorkflowCard card in (workflow.Cards ?? new List<DirectorWorkflowCard>())
            .Where(card => card is not null
                && card.Enabled
                && card.Kind is DirectorWorkflowCardKind.Action or DirectorWorkflowCardKind.ActorPath))
        {
            if (ResolveEntityCard(workflow, card) is not null)
            {
                continue;
            }
            string source = card.ActorKey?.Trim() ?? string.Empty;
            string ghost = GhostActorKey(source);
            if (source.Length > 0 && ghost.Length > 0)
            {
                actors.Add(new DirectorWorkflowGhostActor(ghost, source, null));
            }
        }
        return actors
            .GroupBy(actor => actor.GhostActorKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    public static void NormalizeCardBindings(DirectorWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        foreach (DirectorWorkflowCard card in (workflow.Cards ?? new List<DirectorWorkflowCard>())
            .Where(card => card is not null
                && card.Enabled
                && card.Kind is DirectorWorkflowCardKind.Action or DirectorWorkflowCardKind.ActorPath))
        {
            DirectorWorkflowCard? entityCard = ResolveEntityCard(workflow, card);
            if (entityCard is not null)
            {
                card.BoundCardId = entityCard.Id;
            }
        }
    }
}
