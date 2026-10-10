using System;

namespace U8Gateway.Contracts
{
    /// <summary>
    /// 项目统一业务时钟。除非外部接口明确要求其他时区，所有业务时间使用北京时间。
    /// </summary>
    public static class BusinessClock
    {
        public static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

        public static DateTimeOffset Now =>
            DateTimeOffset.UtcNow.ToOffset(BeijingOffset);

        public static DateTime Today => Now.Date;
    }
}
