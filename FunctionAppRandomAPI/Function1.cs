using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs.Extensions.Http;
using System.Linq;
using System.Text.Json;

namespace FunctionAppRandomAPI
{ 

public static class ApiFetcherFunction
{

    [FunctionName("FetchApiData")]
    public static async Task RunAsync(
        [TimerTrigger("0 */1 * * * *")] TimerInfo timer, Binder binder,
        ILogger log)
    {
        string apiFetchId = DateTime.Now.ToString("yyyyMMddHHmmss");
        bool isSuccess = false;

        HttpClient httpClient = new();
        try
        {
            var response = await httpClient.GetAsync(Environment.GetEnvironmentVariable("apiUrl"));
            response.EnsureSuccessStatusCode();
            isSuccess = response.IsSuccessStatusCode;
            string payload = await response.Content.ReadAsStringAsync();

            if (isSuccess)
            {
                BlobAttribute blobAttribute = new($"{Environment.GetEnvironmentVariable("blobPath")}/{apiFetchId}.json");
                var blobWriter = binder.Bind<TextWriter>(blobAttribute);
                await blobWriter.WriteAsync(payload);
            }
            else
            {
                log.LogError($"Data fetch failed: {response.StatusCode}");
            }

        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error fetching API data");
        }
        finally
        {
            httpClient.Dispose();
        }

        try
        {
            TableServiceClient serviceClient = new(Environment.GetEnvironmentVariable("AzureWebJobsStorage"));
            TableClient tableClient = serviceClient.GetTableClient(Environment.GetEnvironmentVariable("tableName"));

            var tableEntity = new TableEntity("APIFetch", apiFetchId)
        {
            { "Success", isSuccess }
        };

            await tableClient.UpsertEntityAsync(tableEntity);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error saving to Azure table");
        }


    }

    [FunctionName("GetLogs")]
    public static async Task<IActionResult> GetLogs([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logs")] HttpRequest req)
        {
            //log.LogInformation(">>>>> GetLogs");
            DateTime fromDate;
            DateTime toDate;
            string json;

            if (!req.Query.ContainsKey("from") || !req.Query.ContainsKey("to"))
            {
                return new BadRequestObjectResult("Error: Date parameters 'from' and 'to' are required.");
            }

            try
            {
                fromDate = DateTime.Parse(req.Query["from"]);
                toDate   = DateTime.Parse(req.Query["to"]);
            }
            catch (Exception ex)
            {
                return new BadRequestObjectResult($"Error: Malformed parameters in the request ({ex.Message})");
            }

            TableServiceClient serviceClient = new(Environment.GetEnvironmentVariable("AzureWebJobsStorage"));
            TableClient tableClient = serviceClient.GetTableClient(Environment.GetEnvironmentVariable("tableName"));
            
            var queryResultsFilter = tableClient.QueryAsync<TableEntity>(filter: TableClient.CreateQueryFilter($"Timestamp gt {fromDate.ToUniversalTime()} and Timestamp lt {toDate.ToUniversalTime()}"));
            var entities = await queryResultsFilter.ToListAsync();

            var options = new JsonSerializerOptions { WriteIndented = true };
            json = JsonSerializer.Serialize(entities, options);

            return new OkObjectResult(json);
        }

        [FunctionName("GetLogPayload")]
        public static async Task<IActionResult> GetLogPayload([HttpTrigger(AuthorizationLevel.Function, "get", Route = "logs/{logId}")] HttpRequest req,
              Binder binder, 
              string logId
            )
        {
            string payload;

            try
            {
                BlobAttribute blobAttribute = new($"{Environment.GetEnvironmentVariable("blobPath")}/{logId}.json");
                var blobReader = binder.Bind<TextReader>(blobAttribute);

                if (blobReader is null)
                {
                    return new NotFoundObjectResult("Log not found");
                }

                payload = await blobReader.ReadToEndAsync();
            }
            catch (Exception ex)
            {
                return new NotFoundObjectResult($"Log not found ({ex.Message}");
            }

            return new OkObjectResult(payload);
        }

}

}