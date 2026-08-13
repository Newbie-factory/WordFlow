namespace WordFlow.Domain.Learning;

public enum LearningAction
{
    Again,
    Hard,
    Good,
    Slash,
    RestoreScheduled,
    RestoreImmediate,
    Undo,
}

public enum RestoreMode
{
    Scheduled,
    Immediate,
}
