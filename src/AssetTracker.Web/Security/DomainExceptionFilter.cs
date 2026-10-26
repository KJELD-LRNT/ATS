using AssetTracker.Core.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AssetTracker.Web.Security;

/// <summary>Maps domain exceptions to HTTP results. Access denials surface as 404 to avoid leaking existence.</summary>
public class DomainExceptionFilter : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var executed = await next();
        switch (executed.Exception)
        {
            case AccessDeniedException:
                executed.Result = new NotFoundResult();
                executed.ExceptionHandled = true;
                break;
            case ConflictException ce:
                executed.Result = new ConflictObjectResult(ce.Message);
                executed.ExceptionHandled = true;
                break;
            case DomainException de:
                executed.Result = new BadRequestObjectResult(de.Message);
                executed.ExceptionHandled = true;
                break;
        }
    }
}
