/// <summary>
/// JiraEvents — Event types this connector emits.
/// Mapping from Jira webhook "webhookEvent" field to Novara event names.
/// </summary>
namespace Novara.Connector.Jira.Constants;

public static class JiraEvents
{
    // Emitted events (ConnectorDataEvent.EventType)
    public const string IssueCreated  = "jira.issue.created";
    public const string IssueUpdated  = "jira.issue.updated";
    public const string IssueDeleted  = "jira.issue.deleted";
    public const string CommentAdded  = "jira.comment.added";
    public const string SprintStarted = "jira.sprint.started";
    public const string SprintClosed  = "jira.sprint.closed";

    // Jira "webhookEvent" values (inbound)
    public const string JiraIssueCreated  = "jira:issue_created";
    public const string JiraIssueUpdated  = "jira:issue_updated";
    public const string JiraIssueDeleted  = "jira:issue_deleted";
    public const string JiraCommentCreated = "comment_created";
    public const string JiraSprintStarted = "sprint_started";
    public const string JiraSprintClosed  = "sprint_closed";
}
