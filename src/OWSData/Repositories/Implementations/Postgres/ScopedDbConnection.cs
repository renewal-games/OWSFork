using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace OWSData.Repositories.Implementations.Postgres
{
    internal sealed class ScopedDbConnection : DbConnection
    {
        private readonly DbConnection _innerConnection;
        private readonly Action _onDispose;
        private bool _isDisposed;

        public ScopedDbConnection(DbConnection innerConnection, Action onDispose)
        {
            _innerConnection = innerConnection;
            _onDispose = onDispose;
        }

        public override string ConnectionString
        {
            get => _innerConnection.ConnectionString;
            set => _innerConnection.ConnectionString = value;
        }

        public override int ConnectionTimeout => _innerConnection.ConnectionTimeout;

        public override string Database => _innerConnection.Database;

        public override string DataSource => _innerConnection.DataSource;

        public override string ServerVersion => _innerConnection.ServerVersion;

        public override ConnectionState State => _innerConnection.State;

        public override void ChangeDatabase(string databaseName)
        {
            _innerConnection.ChangeDatabase(databaseName);
        }

        public override void Close()
        {
            _innerConnection.Close();
        }

        public override void Open()
        {
            _innerConnection.Open();
        }

        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            return _innerConnection.OpenAsync(cancellationToken);
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            return _innerConnection.BeginTransaction(isolationLevel);
        }

        protected override DbCommand CreateDbCommand()
        {
            return _innerConnection.CreateCommand();
        }

        protected override void Dispose(bool disposing)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            try
            {
                if (disposing)
                {
                    _innerConnection.Dispose();
                }
            }
            finally
            {
                _onDispose();
            }
        }
    }
}
