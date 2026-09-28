namespace DocuEngAIne.Core.Enums;

public enum AccessReviewStatus
{
    Draft = 0,
    InProgress = 1,
    Completed = 2,
    Cancelled = 3,
}

public enum AccessReviewDecision
{
    Pending = 0,
    Retain = 1,
    Revoke = 2,
    ChangeRole = 3,
}
