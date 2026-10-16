namespace DevScope.Api.Domain;

public sealed record GitLabReference(
    string ProjectPath,
    string Branch,
    int MergeRequestIid,
    string MergeRequestState,
    int CommitCount,
    string LinkSource,
    int DiscussionCount = 0);
