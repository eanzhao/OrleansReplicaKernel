namespace OrleansReplicaKernel.Demo;

public interface IEchoObserver
{
    Task OnEchoAsync(string value, CancellationToken cancellationToken = default);
}
