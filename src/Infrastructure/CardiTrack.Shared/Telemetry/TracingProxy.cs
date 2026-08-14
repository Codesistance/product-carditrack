using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.Shared.Telemetry;

/// <summary>
/// Wraps any interface implementation so every method call becomes an <see cref="Activity"/>
/// span, without the wrapped class taking any OpenTelemetry dependency itself. Register through
/// <see cref="TracingServiceCollectionExtensions.AddScopedWithTracing{TInterface,TImplementation}"/>
/// rather than constructing directly. Ported from ConcairgeApp's identically-named class.
/// </summary>
public sealed class TracingProxy<T> : DispatchProxy where T : class
{
    private static readonly ActivitySource Source = new(TelemetryNames.PushSource);
    private static readonly ConcurrentDictionary<Type, MethodInfo> GenericTaskWrappers = new();

    private T _target = null!;
    private string _typeName = string.Empty;

    public static T Create(T target)
    {
        var proxy = (TracingProxy<T>)(object)Create<T, TracingProxy<T>>()!;
        proxy._target = target;
        proxy._typeName = target.GetType().Name;
        return (T)(object)proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
            return null;

        var spanName = $"{_typeName}.{targetMethod.Name}";
        var activity = Source.StartActivity(spanName, ActivityKind.Internal);
        activity?.SetTag("service.type", _typeName);
        activity?.SetTag("service.method", targetMethod.Name);

        try
        {
            var result = targetMethod.Invoke(_target, args);
            return WrapResult(result, targetMethod.ReturnType, activity);
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException ?? ex;
            MarkError(activity, inner);
            activity?.Dispose();
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw; // unreachable
        }
        catch (Exception ex)
        {
            MarkError(activity, ex);
            activity?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sync results dispose the span immediately; <see cref="Task"/>/<see cref="Task{TResult}"/>
    /// results close it only once the awaited call actually completes, so the span's duration
    /// reflects the real work, not just the synchronous part of starting it.
    /// </summary>
    private static object? WrapResult(object? result, Type returnType, Activity? activity)
    {
        if (result is null || returnType == typeof(void))
        {
            activity?.Dispose();
            return result;
        }

        if (returnType == typeof(Task))
            return WrapVoidTaskAsync((Task)result, activity);

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var wrapper = GenericTaskWrappers.GetOrAdd(resultType, static t =>
                typeof(TracingProxy<T>)
                    .GetMethod(nameof(WrapGenericTaskAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(t));
            return wrapper.Invoke(null, [result, activity]);
        }

        activity?.Dispose();
        return result;
    }

    private static async Task WrapVoidTaskAsync(Task task, Activity? activity)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            MarkError(activity, ex);
            throw;
        }
        finally
        {
            activity?.Dispose();
        }
    }

    private static async Task<TResult> WrapGenericTaskAsync<TResult>(Task<TResult> task, Activity? activity)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            MarkError(activity, ex);
            throw;
        }
        finally
        {
            activity?.Dispose();
        }
    }

    private static void MarkError(Activity? activity, Exception ex)
    {
        if (activity is null)
            return;

        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.SetTag("error.type", ex.GetType().FullName);
        activity.SetTag("error.message", ex.Message);
        activity.AddException(ex);
    }
}

public static class TracingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TImplementation"/> as normal, then registers
    /// <typeparamref name="TInterface"/> as a <see cref="TracingProxy{T}"/> wrapping it — every
    /// resolved <typeparamref name="TInterface"/> is the proxy, invisibly emitting a span per
    /// interface method call.
    /// </summary>
    public static IServiceCollection AddScopedWithTracing<TInterface, TImplementation>(
        this IServiceCollection services)
        where TInterface : class
        where TImplementation : class, TInterface
    {
        if (!typeof(TInterface).IsInterface)
            throw new InvalidOperationException(
                $"{typeof(TInterface).Name} must be an interface — TracingProxy wraps interface calls.");

        services.AddScoped<TImplementation>();
        services.AddScoped<TInterface>(sp =>
            TracingProxy<TInterface>.Create(sp.GetRequiredService<TImplementation>()));
        return services;
    }
}
