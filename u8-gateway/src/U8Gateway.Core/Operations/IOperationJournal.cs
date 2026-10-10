using U8Gateway.Contracts.Operations;

namespace U8Gateway.Core.Operations
{
    public interface IOperationJournal
    {
        OperationJournalHandle Begin(OperationJournalStart start);
        void Append(OperationJournalHandle handle, OperationJournalEvent value);
    }
}
