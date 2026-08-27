using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace propseekr_file_processor.Interfaces
{
    public interface IHandleIngest
    {
        Task<APIGatewayProxyResponse> HandleIngestAsync(
    APIGatewayProxyRequest request, ILambdaContext context);
    }
}

