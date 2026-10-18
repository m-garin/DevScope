namespace DevScope.Api.Domain;

public sealed record GitLabReference(
    string ProjectPath,
    string Branch,
    int MergeRequestIid,
    string MergeRequestState,
    int CommitCount,
    string LinkSource,
    int DiscussionCount = 0,
    string? Url = null,
    bool EmployeeApproved = false,
    int EmployeeCommentCount = 0,
    int EmployeeCommitCount = 0,
    bool EmployeeIsReviewer = false,
    bool CommitAuthorsKnown = false,
    IReadOnlyList<string>? OtherCommitAuthors = null);
