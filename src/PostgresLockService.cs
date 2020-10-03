﻿using System;
using System.Data;
using Npgsql;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using ECommon.Components;
using ECommon.DapperEx;
using ECommon.Extensions;
using ECommon.IO;
using ECommon.Utilities;
using ENode.Infrastructure;

namespace ENode.Ex.Postgres
{
    public class PostgresLockService : ILockService
    {
        #region Private Variables

        private string _connectionString;
        private string _tableName;
        private string _lockKeySqlFormat;
        private IOHelper _ioHelper;

        #endregion

        public PostgresLockService Initialize(string connectionString, string tableName = "LockKey")
        {
            _connectionString = connectionString;
            _tableName = tableName ;

            Ensure.NotNull(_connectionString, "_connectionString");
            Ensure.NotNull(_tableName, "_tableName");

            _tableName = string.Format("\"{0}\"", _tableName);

            _lockKeySqlFormat = "SELECT * FROM " + _tableName + "  WHERE Name = '{0}' FOR UPDATE";

            _ioHelper = ObjectContainer.Resolve<IOHelper>();

            return this;
        }
        public async Task AddLockKey(string lockKey)
        {
            await _ioHelper.TryIOActionAsync(async () =>
            {
                using (var connection = GetConnection())
                {
                    await connection.OpenAsync().ConfigureAwait(false);
                    var list = await connection.QueryListAsync(new { Name = lockKey }, _tableName);
                    if (list.Count() == 0)
                    {
                        await connection.InsertAsync(new { Name = lockKey }, _tableName);
                    }
                }
            }, "AddLockKey");
        }
        public async Task ExecuteInLock(string lockKey, Func<Task> action)
        {
            try
            {
                using (var connection = GetConnection())
                {
                    await connection.OpenAsync().ConfigureAwait(false);
                    var transaction = await Task.Run(() => connection.BeginTransaction()).ConfigureAwait(false);
                    try
                    {
                        await LockKey(transaction, lockKey);
                        await action();
                        await Task.Run(() => transaction.Commit()).ConfigureAwait(false);
                    }
                    catch
                    {
                        await Task.Run(() => transaction.Rollback()).ConfigureAwait(false);
                        throw;
                    }
                }
            }
            catch (AggregateException aggregateException)
            {
                if (aggregateException.InnerExceptions.IsNotEmpty()
                 && aggregateException.InnerExceptions.Any(x => x is NpgsqlException))
                {
                    throw new IOException("ExecuteInLock has io exception, lockKey: " + lockKey, aggregateException);
                }
            }
            catch (NpgsqlException ex)
            {
                throw new IOException("ExecuteInLock has io exception, lockKey: " + lockKey, ex);
            }
        }

        private Task LockKey(IDbTransaction transaction, string key)
        {
            var sql = string.Format(_lockKeySqlFormat, key);
            return transaction.Connection.QueryAsync(sql, transaction: transaction);
        }
        private NpgsqlConnection GetConnection()
        {
            return new NpgsqlConnection(_connectionString);
        }
    }
}
