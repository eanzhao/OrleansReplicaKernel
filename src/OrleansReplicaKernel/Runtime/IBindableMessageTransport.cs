namespace OrleansReplicaKernel.Runtime;

public interface IBindableMessageTransport
{
    void Bind(IMessageReceiver requestReceiver, IResponseReceiver responseReceiver);
}
