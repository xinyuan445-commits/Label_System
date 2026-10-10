using System;

namespace U8Gateway.Contracts
{
    public sealed class ApiResponse<T>
    {
        public bool Success { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public string RequestId { get; set; }
        public DateTimeOffset ServerTime { get; set; }
        public T Data { get; set; }

        public static ApiResponse<T> Ok(T data, string requestId, string message = null)
        {
            return new ApiResponse<T>
            {
                Success = true,
                Code = "OK",
                Message = message ?? "成功",
                RequestId = requestId,
                ServerTime = BusinessClock.Now,
                Data = data
            };
        }
    }
}
