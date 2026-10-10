using System;
using System.Threading.Tasks;

namespace U8Gateway.Core.U8
{
    public interface IU8OperationQueue
    {
        Task<T> ExecuteAsync<T>(Func<T> action);
    }
}
