/// <summary>
/// JiraConnector — Atlassian Jira Integration for Novara
///
/// PURPOSE: Self-describing Jira Cloud / Jira Server 8+ connector. Receives issue,
/// comment, and sprint webhooks and emits normalized ConnectorDataEvents consumed
/// by the Issues and Roadmap modules.
///
/// AUTH: Jira Cloud uses email + API token (basic auth). Jira Server 8+ uses PAT.
/// Both are captured via manifest config fields. Webhooks on Jira Cloud do NOT carry
/// HMAC signatures, so a shared secret header is used instead — Jira Automation rules
/// or the Webhook UI lets admins add custom headers.
///
/// WEBHOOK FLOW:
///   1. Jira POSTs to /api/v1/connectors/connector.jira/webhook
///   2. Gateway routes to this connector's HandleWebhookAsync
///   3. We verify the X-Novara-Token header matches the configured token
///   4. We dispatch by the "webhookEvent" field in the body
///   5. We emit one ConnectorDataEvent per normalized change
/// </summary>
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Novara.Module.SDK;
using Novara.Connector.Jira.Constants;
using Novara.Connector.Jira.Models;

namespace Novara.Connector.Jira;

public class JiraConnector : ConnectorBase
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public override ConnectorManifest Manifest => new()
    {
        Id = "connector.jira",
        Name = "Jira",
        Version = "2026.4.22.1",
        Author = "Monocept",
        Description = "Atlassian Jira integration — issue, comment, and sprint webhooks. Bidirectional sync with the Issues and Roadmap modules.",
        Icon = "jira",
        Source = "Official",
        Category = "PM",
        AuthType = "basic",
        DocumentationUrl = "https://docs.novara.io/connectors/jira",
        SupportsImport = true,
        SupportsExport = true,
        SupportsWebhook = true,
        SupportedEventTypes = new()
        {
            JiraEvents.IssueCreated, JiraEvents.IssueUpdated, JiraEvents.IssueDeleted,
            JiraEvents.CommentAdded,
            JiraEvents.SprintStarted, JiraEvents.SprintClosed
        },
        TargetModules = new() { "novara.issues", "novara.roadmap", "novara.collaborate" },
        ConfigFields = new()
        {
            new() { Key = "baseUrl", Label = "Jira Base URL", Type = "url",
                    Required = true,
                    Placeholder = "https://your-org.atlassian.net",
                    Description = "Jira Cloud URL or on-prem Jira Server URL.",
                    Group = "Connection", Order = 1 },
            new() { Key = "email", Label = "Email (Cloud) / Username (Server)", Type = "text",
                    Required = true,
                    Placeholder = "you@example.com",
                    Description = "For Jira Cloud: the email tied to the API token. For Jira Server 8+: your username.",
                    Group = "Authentication", Order = 2 },
            new() { Key = "apiToken", Label = "API Token / Personal Access Token",
                    Type = "password", Required = true, Sensitive = true,
                    Description = "Jira Cloud: https://id.atlassian.com/manage-profile/security/api-tokens. Jira Server: Profile → Personal Access Tokens.",
                    Group = "Authentication", Order = 3 },
            new() { Key = "webhookSecret", Label = "Webhook Secret Token",
                    Type = "password", Required = true, Sensitive = true,
                    Description = "Shared secret Jira will send in the X-Novara-Token header. Configure this as a custom header on your Jira webhook.",
                    Group = "Authentication", Order = 4 },
            new() { Key = "projectKeys", Label = "Project Keys", Type = "text",
                    Required = false,
                    Placeholder = "NOV,INFRA",
                    Description = "Comma-separated project keys to accept. Empty = all projects.",
                    Group = "Scope", Order = 5 },
            new() { Key = "issueTypes", Label = "Issue Types", Type = "text",
                    Required = false,
                    Placeholder = "Bug,Story,Task",
                    Description = "Comma-separated issue types to accept. Empty = all types.",
                    Group = "Scope", Order = 6 }
        }
    };

    public override async Task<ConnectorTestResult> TestConnectionAsync(ConnectorConfig config, CancellationToken ct = default)
    {
        var baseUrl = config.Values.GetValueOrDefault("baseUrl", "");
        var email = config.Values.GetValueOrDefault("email", "");
        var token = config.Values.GetValueOrDefault("apiToken", "");

        if (string.IsNullOrEmpty(baseUrl))
            return new ConnectorTestResult { Success = false, Message = "Jira Base URL is required." };
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(token))
            return new ConnectorTestResult { Success = false, Message = "Email and API token are required." };

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{token}"));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/rest/api/3/myself", ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                return new ConnectorTestResult
                {
                    Success = false,
                    Message = $"Jira returned {(int)response.StatusCode}: {body}"
                };
            }

            var myself = await response.Content.ReadFromJsonAsync<JiraMyself>(JsonOpts, ct);
            return new ConnectorTestResult
            {
                Success = true,
                Message = $"Connected as {myself?.DisplayName ?? myself?.EmailAddress ?? "unknown"}.",
                ServerVersion = "Jira Cloud / Server 8+"
            };
        }
        catch (HttpRequestException ex)
        {
            return new ConnectorTestResult { Success = false, Message = $"Cannot reach Jira at {baseUrl}: {ex.Message}" };
        }
        catch (TaskCanceledException)
        {
            return new ConnectorTestResult { Success = false, Message = $"Timeout reaching Jira at {baseUrl}." };
        }
    }

    public override Task<ConnectorResult> HandleWebhookAsync(
        ConnectorConfig config, string payload, Dictionary<string, string> headers, CancellationToken ct = default)
    {
        var result = new ConnectorResult { Success = true };

        // Shared-secret auth via X-Novara-Token (Jira Cloud supports custom webhook headers).
        var expected = config.Values.GetValueOrDefault("webhookSecret", "");
        var presented = headers.GetValueOrDefault("X-Novara-Token",
                        headers.GetValueOrDefault("x-novara-token", ""));
        if (!string.IsNullOrEmpty(expected) && !string.Equals(presented, expected, StringComparison.Ordinal))
        {
            result.Success = false;
            result.Message = "Invalid X-Novara-Token.";
            return Task.FromResult(result);
        }

        try
        {
            // Dispatch by webhookEvent in body. Jira sends different top-level shapes
            // for sprint events vs issue/comment events — parse conservatively.
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("webhookEvent", out var eventProp))
            {
                result.Success = false;
                result.Message = "Missing webhookEvent field.";
                return Task.FromResult(result);
            }

            var webhookEvent = eventProp.GetString() ?? "";
            switch (webhookEvent)
            {
                case JiraEvents.JiraIssueCreated:
                    result.Events.AddRange(ParseIssueEvent(payload, JiraEvents.IssueCreated, config));
                    break;
                case JiraEvents.JiraIssueUpdated:
                    result.Events.AddRange(ParseIssueEvent(payload, JiraEvents.IssueUpdated, config));
                    break;
                case JiraEvents.JiraIssueDeleted:
                    result.Events.AddRange(ParseIssueEvent(payload, JiraEvents.IssueDeleted, config));
                    break;
                case JiraEvents.JiraCommentCreated:
                    result.Events.AddRange(ParseCommentEvent(payload));
                    break;
                case JiraEvents.JiraSprintStarted:
                    result.Events.AddRange(ParseSprintEvent(payload, JiraEvents.SprintStarted));
                    break;
                case JiraEvents.JiraSprintClosed:
                    result.Events.AddRange(ParseSprintEvent(payload, JiraEvents.SprintClosed));
                    break;
                default:
                    result.Message = $"Event type '{webhookEvent}' not handled.";
                    break;
            }

            result.RecordsProcessed = result.Events.Count;
        }
        catch (JsonException ex)
        {
            result.Success = false;
            result.Message = $"Failed to parse Jira payload: {ex.Message}";
        }

        return Task.FromResult(result);
    }

    public override IEnumerable<FieldMapping> GetDefaultFieldMappings() => new[]
    {
        // Issue → Issue
        new FieldMapping { NovaraField = "Title",       ExternalField = "issue.fields.summary" },
        new FieldMapping { NovaraField = "Description", ExternalField = "issue.fields.description" },
        new FieldMapping { NovaraField = "Status",      ExternalField = "issue.fields.status.name",
            Transform = "StatusMap",
            TransformConfig = "{\"To Do\":\"Open\",\"In Progress\":\"InProgress\",\"Done\":\"Resolved\",\"Closed\":\"Closed\"}" },
        new FieldMapping { NovaraField = "Severity",    ExternalField = "issue.fields.priority.name",
            Transform = "StatusMap",
            TransformConfig = "{\"Highest\":\"Critical\",\"High\":\"High\",\"Medium\":\"Medium\",\"Low\":\"Low\",\"Lowest\":\"Trivial\"}" },
        new FieldMapping { NovaraField = "Assignee",    ExternalField = "issue.fields.assignee.displayName", Transform = "UserLookup" },
        new FieldMapping { NovaraField = "Reporter",    ExternalField = "issue.fields.reporter.displayName", Transform = "UserLookup" },
        new FieldMapping { NovaraField = "Labels",      ExternalField = "issue.fields.labels" },
        new FieldMapping { NovaraField = "ExternalId",  ExternalField = "issue.key" },
        // Sprint → Track / Milestone (module decides)
        new FieldMapping { NovaraField = "Name",        ExternalField = "sprint.name" },
        new FieldMapping { NovaraField = "StartDate",   ExternalField = "sprint.startDate" },
        new FieldMapping { NovaraField = "EndDate",     ExternalField = "sprint.endDate" }
    };

    private static List<ConnectorDataEvent> ParseIssueEvent(string payload, string eventType, ConnectorConfig config)
    {
        var ev = JsonSerializer.Deserialize<JiraIssueEvent>(payload, JsonOpts);
        if (ev?.Issue == null) return new();

        // Optional project / issue type filter
        var allowedProjects = SplitCsv(config.Values.GetValueOrDefault("projectKeys", ""));
        var allowedTypes = SplitCsv(config.Values.GetValueOrDefault("issueTypes", ""));
        var projectKey = ev.Issue.Fields.Project?.Key ?? "";
        var issueType = ev.Issue.Fields.IssueType?.Name ?? "";

        if (allowedProjects.Count > 0 && !allowedProjects.Contains(projectKey, StringComparer.OrdinalIgnoreCase))
            return new();
        if (allowedTypes.Count > 0 && !allowedTypes.Contains(issueType, StringComparer.OrdinalIgnoreCase))
            return new();

        return new()
        {
            new ConnectorDataEvent
            {
                ConnectorId = "connector.jira",
                EventType = eventType,
                EntityType = "issue",
                ExternalId = ev.Issue.Key,
                ExternalUrl = BuildIssueUrl(ev.Issue, config),
                TimestampUtc = ev.Issue.Fields.Updated ?? ev.Issue.Fields.Created ?? DateTime.UtcNow,
                Data = new Dictionary<string, object>
                {
                    ["key"]         = ev.Issue.Key,
                    ["id"]          = ev.Issue.Id,
                    ["summary"]     = ev.Issue.Fields.Summary ?? "",
                    ["description"] = ev.Issue.Fields.Description ?? "",
                    ["status"]      = ev.Issue.Fields.Status?.Name ?? "",
                    ["statusCategory"] = ev.Issue.Fields.Status?.StatusCategory?.Key ?? "",
                    ["priority"]    = ev.Issue.Fields.Priority?.Name ?? "",
                    ["issueType"]   = issueType,
                    ["project"]     = projectKey,
                    ["assignee"]    = ev.Issue.Fields.Assignee?.DisplayName ?? "",
                    ["reporter"]    = ev.Issue.Fields.Reporter?.DisplayName ?? "",
                    ["labels"]      = ev.Issue.Fields.Labels
                }
            }
        };
    }

    private static List<ConnectorDataEvent> ParseCommentEvent(string payload)
    {
        var ev = JsonSerializer.Deserialize<JiraIssueEvent>(payload, JsonOpts);
        if (ev?.Comment == null || ev.Issue == null) return new();

        return new()
        {
            new ConnectorDataEvent
            {
                ConnectorId = "connector.jira",
                EventType = JiraEvents.CommentAdded,
                EntityType = "comment",
                ExternalId = $"{ev.Issue.Key}#{ev.Comment.Id}",
                TimestampUtc = ev.Comment.Created ?? DateTime.UtcNow,
                Data = new Dictionary<string, object>
                {
                    ["issueKey"] = ev.Issue.Key,
                    ["body"]     = ev.Comment.Body,
                    ["author"]   = ev.Comment.Author?.DisplayName ?? "",
                    ["created"]  = ev.Comment.Created!
                }
            }
        };
    }

    private static List<ConnectorDataEvent> ParseSprintEvent(string payload, string eventType)
    {
        var ev = JsonSerializer.Deserialize<JiraSprintEvent>(payload, JsonOpts);
        if (ev?.Sprint == null) return new();

        return new()
        {
            new ConnectorDataEvent
            {
                ConnectorId = "connector.jira",
                EventType = eventType,
                EntityType = "sprint",
                ExternalId = $"sprint-{ev.Sprint.Id}",
                TimestampUtc = ev.Sprint.CompleteDate ?? ev.Sprint.StartDate ?? DateTime.UtcNow,
                Data = new Dictionary<string, object>
                {
                    ["id"]            = ev.Sprint.Id,
                    ["name"]          = ev.Sprint.Name,
                    ["state"]         = ev.Sprint.State,
                    ["startDate"]     = ev.Sprint.StartDate!,
                    ["endDate"]       = ev.Sprint.EndDate!,
                    ["completeDate"]  = ev.Sprint.CompleteDate!,
                    ["goal"]          = ev.Sprint.Goal ?? "",
                    ["boardId"]       = ev.Sprint.OriginBoardId
                }
            }
        };
    }

    private static string? BuildIssueUrl(JiraIssue issue, ConnectorConfig config)
    {
        var baseUrl = config.Values.GetValueOrDefault("baseUrl", "");
        if (string.IsNullOrEmpty(baseUrl)) return null;
        return $"{baseUrl.TrimEnd('/')}/browse/{issue.Key}";
    }

    private static List<string> SplitCsv(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new();
        return s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}
