using System;
using System.Configuration;
using System.Threading.Tasks;

namespace JiraToDevOps
{
    class Program
    {
        static async Task Main(string[] args)
        {
            string devOpsUrl = ConfigurationManager.AppSettings["devOpsUrl"];
            string devOpsPersonalAccessToken = ConfigurationManager.AppSettings["devOpsPersonalAccessToken"];
            string devOpsProjectName = ConfigurationManager.AppSettings["devOpsProjectName"];

            string jiraUrl = ConfigurationManager.AppSettings["jiraUrl"];
            string jiraUserID = ConfigurationManager.AppSettings["jiraUserID"];
            string jiraAccessToken = ConfigurationManager.AppSettings["jiraAccessToken"];
            string jiraProjectAbbrev = ConfigurationManager.AppSettings["jiraProjectAbbrev"];

            var migrator = new Migrator(devOpsUrl, devOpsPersonalAccessToken, devOpsProjectName, jiraUrl, jiraUserID, jiraAccessToken, jiraProjectAbbrev);
            await migrator.MigrateOpenIssuesByJQL($"project={jiraProjectAbbrev} AND status != Done").ConfigureAwait(false);
        }
    }
}
