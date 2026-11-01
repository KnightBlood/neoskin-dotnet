namespace NeoSkin.Web.Support;

/// <summary>
/// PHP 错误处理器把 warning 提升成异常后的载体（Laravel <c>ErrorException</c>）。
/// 目前用于 <c>unserialize()</c> 的 warning（会话数据损坏时抛出）。
/// </summary>
public sealed class ErrorException(string message, Exception? inner = null) : Exception(message, inner);
