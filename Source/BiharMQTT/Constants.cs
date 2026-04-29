namespace BiharMQTT;

public static class Constants
{
  public const int MaxMemoryPerMessageBytes = 256;
  public const int MaxPendingMessagesPerSession = 2048;
  public const int PreAllocatedQMemoryKb = MaxMemoryPerMessageBytes * MaxPendingMessagesPerSession;
  public const int MaxConcurrentConnections = 250;
  public const int PerBufferWriterMemoryAllocatedbytes = 2048;
}