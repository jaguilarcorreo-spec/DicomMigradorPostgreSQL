using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace DicomMigrator.Web.Components;

/// <summary>
/// Base de las páginas interactivas (UI-7): red de seguridad para TODOS sus manejadores de
/// eventos (botones, cambios de campo, enlaces…), los de hoy y los que se añadan.
///
/// En Blazor Server, una excepción sin capturar en un manejador cierra el circuito: sale la
/// barra «An unhandled error has occurred», la pestaña deja de responder y hay que recargar,
/// perdiendo filtros y formularios. Un ErrorBoundary no sirve aquí: solo captura errores de
/// los componentes que tiene DEBAJO, y los manejadores pertenecen a la propia página (que
/// quedaría por encima); el layout no es interactivo, así que tampoco puede ir en él.
///
/// Esta base re-implementa <see cref="IHandleEvent"/>, el punto por el que Blazor ejecuta
/// cada manejador de la página, con el mismo comportamiento que ComponentBase (repintar antes
/// y después de la parte asíncrona) pero capturando la excepción: se registra en el log y se
/// muestra en un aviso flotante (<see cref="GuardError"/>, componente ActionErrorToast). La
/// página sigue viva. Los manejadores pueden seguir capturando sus errores para dar un
/// mensaje más concreto; esto es solo para lo que se escape.
/// </summary>
public abstract class GuardedPage : ComponentBase, IHandleEvent
{
    [Inject] private ILogger<GuardedPage> GuardLogger { get; set; } = default!;

    /// <summary>Mensaje del último error no controlado de un manejador, o null.</summary>
    protected string? GuardError { get; private set; }

    /// <summary>Cierra el aviso.</summary>
    protected void ClearGuardError() => GuardError = null;

    Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? arg)
    {
        Task task;
        try
        {
            task = callback.InvokeAsync(arg);
        }
        catch (Exception ex)
        {
            Report(ex);
            StateHasChanged();
            return Task.CompletedTask;
        }

        // Igual que ComponentBase: si el manejador termina en síncrono, un repintado; si no,
        // uno ahora y otro al terminar.
        var shouldAwait = task.Status != TaskStatus.RanToCompletion && task.Status != TaskStatus.Canceled;
        StateHasChanged();
        return shouldAwait ? AwaitAndRenderAsync(task) : Task.CompletedTask;
    }

    private async Task AwaitAndRenderAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (task.IsCanceled)
        {
            return;   // como ComponentBase: una cancelación no es un error ni repinta
        }
        catch (Exception ex)
        {
            Report(ex);
        }
        StateHasChanged();
    }

    private void Report(Exception ex)
    {
        // Primero el aviso: el registro en el log no debe poder impedirlo.
        GuardError = ex.GetBaseException().Message;
        try { GuardLogger?.LogError(ex, "Error no controlado en un manejador de {Page}; la página sigue activa.", GetType().Name); }
        catch { /* sin log, el aviso ya está puesto */ }
    }
}
