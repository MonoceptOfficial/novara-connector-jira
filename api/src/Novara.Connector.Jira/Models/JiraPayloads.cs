/// <summary>
/// JiraPayloads — Strongly-typed shapes for Jira webhook payloads.
///
/// REFERENCE: https://developer.atlassian.com/server/jira/platform/webhooks/
/// Schemas match the JSON that Jira Cloud (and Server 8+) sends to registered webhooks.
/// Only the fields Novara consumes are modeled — the rest are ignored.
/// </summary>
using System.Text.Json.Serialization;

namespace Novara.Connector.Jira.Models;

public class JiraIssueEvent
{
    public string WebhookEvent { get; set; } = "";     // "jira:issue_created" etc.
    public long Timestamp { get; set; }
    public JiraUser? User { get; set; }
    public JiraIssue? Issue { get; set; }
    public JiraComment? Comment { get; set; }
}

public class JiraIssue
{
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";               // e.g. "NOV-42"
    public string Self { get; set; } = "";              // API URL
    public JiraIssueFields Fields { get; set; } = new();
}

public class JiraIssueFields
{
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public JiraIssueType? IssueType { get; set; }
    public JiraStatus? Status { get; set; }
    public JiraPriority? Priority { get; set; }
    public JiraUser? Assignee { get; set; }
    public JiraUser? Reporter { get; set; }
    public JiraProject? Project { get; set; }
    public List<string> Labels { get; set; } = new();
    public DateTime? Created { get; set; }
    public DateTime? Updated { get; set; }
}

public class JiraIssueType
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";              // Bug, Story, Task, Epic
}

public class JiraStatus
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";              // To Do, In Progress, Done
    public JiraStatusCategory? StatusCategory { get; set; }
}

public class JiraStatusCategory
{
    public string Key { get; set; } = "";               // new, indeterminate, done
    public string Name { get; set; } = "";
}

public class JiraPriority
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";              // Highest, High, Medium, Low, Lowest
}

public class JiraUser
{
    public string AccountId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? EmailAddress { get; set; }
}

public class JiraProject
{
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
}

public class JiraComment
{
    public string Id { get; set; } = "";
    public string Body { get; set; } = "";
    public JiraUser? Author { get; set; }
    public DateTime? Created { get; set; }
}

public class JiraSprintEvent
{
    public string WebhookEvent { get; set; } = "";
    public JiraSprint? Sprint { get; set; }
}

public class JiraSprint
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string State { get; set; } = "";             // future, active, closed
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime? CompleteDate { get; set; }
    public int OriginBoardId { get; set; }
    public string? Goal { get; set; }
}

public class JiraMyself
{
    public string AccountId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? EmailAddress { get; set; }
}
