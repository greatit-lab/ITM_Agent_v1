// ITM_Agent/Plugins/IPluginMetadata.cs
namespace ITM_Agent.Plugins
{
    /// <summary>
    /// 플러그인이 UI에 기본 작업 이름을 제공할 수 있도록 하는 인터페이스
    /// </summary>
    public interface IPluginMetadata
    {
        string DefaultTaskName { get; }
    }

    /// <summary>
    /// 플러그인이 증분 감시(탭 2)에 필요한 기본 작업 이름과 파일 필터를 제공하는 인터페이스
    /// </summary>
    public interface IIncrementalPluginMetadata : IPluginMetadata
    {
        string DefaultFileFilter { get; }
    }
}
