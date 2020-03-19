using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Atlassian.Jira;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;

namespace JiraToDevOps
{
    public class Migrator
    {
        private readonly Jira _jiraClient;
        private readonly WorkItemTrackingHttpClient _witClient;

        private readonly string _devOpsProjectName;
        private readonly string _jiraProjectAbbrev;

        public Migrator(string devOpsUrl, string devOpsPersonalAccessToken, string devOpsProjectName, string jiraUrl, string jiraUserId, string jiraAccessToken, string jiraProjectAbbrev)
        {
            var devOpsConn = new VssConnection(new Uri(devOpsUrl), new VssBasicCredential(string.Empty, devOpsPersonalAccessToken));

            _devOpsProjectName = devOpsProjectName;
            _jiraProjectAbbrev = jiraProjectAbbrev;

            _witClient = devOpsConn.GetClient<WorkItemTrackingHttpClient>();
            _jiraClient = Jira.CreateRestClient(jiraUrl, jiraUserId, jiraAccessToken);
        }

        public async Task MigrateOpenIssuesByJQL(string jql)
        {
            var issues = await _jiraClient.Issues.GetIssuesFromJqlAsync(jql, maxIssues:100).ConfigureAwait(false);

            foreach (var issue in issues)
            {
                await MigrateIssue(issue, issue.Type.Name, null).ConfigureAwait(false);
            }
        }

        private async Task CreateWorkItem(string type, Issue jiraIssue, string parentKey, string title, string description, string state, params JsonPatchOperation[] fields)
        {
            var patchDocument = new JsonPatchDocument
                {   new JsonPatchOperation { Path = "/fields/System.State", Value = state },
                    new JsonPatchOperation { Path = "/fields/System.CreatedBy", Value = MapUser(jiraIssue.ReporterUser.DisplayName) },
                    new JsonPatchOperation { Path = "/fields/System.CreatedDate", Value = jiraIssue.Created.Value.ToUniversalTime() },
                    new JsonPatchOperation { Path = "/fields/System.ChangedBy", Value = MapUser(jiraIssue.ReporterUser.DisplayName) },
                    new JsonPatchOperation { Path = "/fields/System.ChangedDate", Value = jiraIssue.Created.Value.ToUniversalTime() },
                    new JsonPatchOperation { Path = "/fields/System.Title", Value = title },
                    new JsonPatchOperation { Path = "/fields/System.Description", Value = description },
                    new JsonPatchOperation { Path = "/fields/Custom.JiraID", Value = jiraIssue.Key.Value },
                    new JsonPatchOperation { Path = "/fields/Microsoft.VSTS.Common.Priority", Value = MapPriority(jiraIssue.Priority) }
            };

            if (jiraIssue.AdditionalFields["customfield_10301"] != null)
            {
                patchDocument.Add(new JsonPatchOperation { Path = "/fields/Custom.TestURL", Value = jiraIssue.AdditionalFields["customfield_10301"]?.ToString() });
            }

            if (parentKey != null)
            {
                patchDocument.Add(new JsonPatchOperation { Path = "/relations/-", Value = new WorkItemRelation { Rel = "System.LinkTypes.Hierarchy-Reverse", Url = $"https://ciappdev.visualstudio.com/_apis/wit/workItems/{parentKey}"}});
            }

            if (jiraIssue.AssigneeUser != null)
            {
                patchDocument.Add(new JsonPatchOperation { Path = "/fields/System.AssignedTo", Value = MapUser(jiraIssue.AssigneeUser.DisplayName) });
            }

            patchDocument.Add(new JsonPatchOperation { Path = "/fields/System.Tags", Value = _jiraProjectAbbrev });

            var attachments = await _jiraClient.Issues.GetAttachmentsAsync(jiraIssue.JiraIdentifier).ConfigureAwait(false);

            if (attachments != null)
            {
                foreach (var attachment in attachments)
                {
                    var bytes = attachment.DownloadData();

                    using var stream = new MemoryStream(bytes);
                    var uploaded = await _witClient.CreateAttachmentAsync(stream, _devOpsProjectName, fileName: attachment.FileName).ConfigureAwait(false);
                    patchDocument.Add(new JsonPatchOperation { Path = "/relations/-", Value = new WorkItemRelation { Rel = "AttachedFile", Url = uploaded.Url } });
                }
            }

            var all = patchDocument.Concat(fields).Where(p => p.Value != null).ToList();

            patchDocument = new JsonPatchDocument();

            patchDocument.AddRange(all);

            try
            {
                var workItem = await _witClient.CreateWorkItemAsync(patchDocument, _devOpsProjectName, type, bypassRules: true).ConfigureAwait(false);

                await CreateComments(workItem.Id.Value, jiraIssue).ConfigureAwait(false);

                Console.WriteLine($"Added {type}: {jiraIssue.Key} {title}");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private async Task CreateComments(int workItemId, Issue jiraIssue)
        {
            var comments = await _jiraClient.Issues.GetCommentsAsync(jiraIssue.JiraIdentifier).ConfigureAwait(false);

            foreach (var comment in comments?.Reverse())
            {
                var commentPatch = CreateCommentPatch(comment.Body, MapUser(comment.AuthorUser.DisplayName), comment.CreatedDate);

                await _witClient.UpdateWorkItemAsync(commentPatch, workItemId, bypassRules: true).ConfigureAwait(false);
            }
        }

        private JsonPatchDocument CreateCommentPatch(string comment, string username = null, DateTime? date = null)
        {
            var patch = new JsonPatchDocument { new JsonPatchOperation { Path = "/fields/System.History", Value = comment } };
            if (username != null) patch.Add(new JsonPatchOperation { Path = "/fields/System.ChangedBy", Value = username });
            //if (date != null) patch.Add(new JsonPatchOperation { Path = "/fields/System.ChangedDate", Value = date?.ToUniversalTime() });

            return patch;
        }

        private async Task MigrateIssue(Issue jiraIssue, string issueType, string defaultParentKey)
        {
            await CreateWorkItem(issueType, jiraIssue, jiraIssue.ParentIssueKey ?? defaultParentKey, jiraIssue.Summary, jiraIssue.Description, MapTaskState(jiraIssue.Status)).ConfigureAwait(false);
        }

        private string MapTaskState(IssueStatus state)
        {
            switch (state.Name.ToLowerInvariant())
            {
                case "uat":
                case "in progress":
                    return "Doing";
                case "done":
                    return "Done";
                case "on hold":
                    return "Removed";
                default:
                    return "To Do";
            }
        }
        private string MapUser(string user)
        {
            var parts = user.ToLower().Split(" ", StringSplitOptions.RemoveEmptyEntries);

            return $"{parts[0]}.{parts[1]}@domain.com";
        }

        private int MapPriority(IssuePriority priority)
        {
            return 3;
        }
    }
}
