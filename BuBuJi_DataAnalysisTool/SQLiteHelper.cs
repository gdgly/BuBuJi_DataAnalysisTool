﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Threading;


namespace BuBuJi_DataAnalysisTool
{
    class SQLiteHelper
    {
        /// <summary>
        /// 连接字符串
        /// </summary>
        private static string _connectionString;
        private static SQLiteConnection _connectionCurrent;
        private static SQLiteConnection _connectionDisk;
        private static SQLiteConnection _connectionMemory;
        private static ConcurrentQueue<SQLiteCommand> _noneQuerySqlQueue;
        private static ConcurrentQueue<SQLiteCommand> _sqlCmdPool;
        private static SQLiteCommand cmd;
        private const int SqlCmdPoolSize = 500000;
        private static Thread _threadWriteDB;
        private static bool _isThreadWriteDbRuning;
        private static bool _isClosingDB;
        public string ConnectionString{ get {return _connectionString;} }
        public SQLiteConnection ConnectionDisk { get { return _connectionDisk; } }

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="_connectionString">连接SQLite库字符串</param>
        public SQLiteHelper(string connStr)
        {
            _connectionString = connStr;
            _noneQuerySqlQueue = new ConcurrentQueue<SQLiteCommand>();
            _sqlCmdPool = new ConcurrentQueue<SQLiteCommand>();
            SQLiteCommand cmd;
            for(int i = 0 ; i < SqlCmdPoolSize; i++)
            {
                cmd = new SQLiteCommand();
                _sqlCmdPool.Enqueue(cmd);
            }
        }

        public SQLiteHelper(string datasource, string version, string password)
            :this(string.Format("Data Source={0};Version={1};password={2}",datasource, version, password))
        {
        }

        /// <summary>
        /// 创建数据库文件
        /// </summary>
        /// <param name="dbName">数据库名</param>
        /// <param name="password">密码</param>
        /// <returns></returns>
        public bool CreateDb(string dbName, string password = "")
        {
            if (string.IsNullOrEmpty(dbName)) return false;

            try
            {
                if (false == File.Exists(dbName))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dbName));
                    SQLiteConnection.CreateFile(dbName);
                }

                if(false == string.IsNullOrEmpty(password))
                {
                    SQLiteConnection con = new SQLiteConnection("data source=" + dbName);
                    con.SetPassword(password);
                    con.Close();
                }
            }
            catch (Exception) { return false; }

            return true;
        }

        /// <summary>
        /// 获取连接
        /// </summary>
        /// <returns>当前数据库连接</returns>
        public SQLiteConnection OpenConnection()
        {
            try
            {
                if (_connectionCurrent == null)
                {
                    _connectionDisk = new SQLiteConnection(ConnectionString);
                    _connectionCurrent = _connectionDisk;
                    _connectionCurrent.Open();
                }
                if(_connectionCurrent.State != ConnectionState.Open)
                {
                    _connectionCurrent.Open();
                }
                return _connectionCurrent;
            }
            catch (Exception) { throw; }
        }

        /// <summary>
        /// 关闭数据库连接
        /// </summary>
        public void CloseConnection()
        {
            try
            {
                if (_connectionMemory != null)
                {
                    _connectionMemory.Close();
                    _connectionMemory = null;
                }

                if (_isThreadWriteDbRuning)
                {
                    StopWriteDbThread();
                }
                else if (_connectionDisk != null)
                {
                    _connectionDisk.Close();
                    _connectionDisk = null;
                }

                _connectionCurrent = null;
            }
            catch (Exception) { throw; }
        }

        /// <summary>
        /// 使用内存数据库
        /// </summary>
        public void MemoryDatabaseEable()
        {
            try
            {
                if (_connectionMemory == null)
                {
                    _connectionMemory = new SQLiteConnection("Data Source = :memory:");
                    _connectionCurrent = _connectionMemory;
                    _connectionMemory.Open(); // allways open
                    if (_connectionDisk == null)
                    {
                        _connectionDisk = new SQLiteConnection(ConnectionString);
                    }
                    if (_connectionDisk.State != ConnectionState.Open)
                    {
                        _connectionDisk.Open();
                    }
                    _connectionDisk.BackupDatabase(_connectionMemory, "main", "main", -1, null, -1);
                    _connectionDisk.Close();

                    StartWriteDbThread();
                }
            }
            catch (Exception) { throw; }
        }
        /// <summary>
        /// 禁用内存数据库
        /// </summary>
        public void MemoryDatabaseDisable()
        {
            try
            {
                MemoryDatabaseToDisk();

                if (_connectionMemory != null)
                {
                    if (_connectionDisk.State != ConnectionState.Open)
                    {
                        _connectionDisk.Open();
                    }
                    _connectionCurrent = _connectionDisk;
                    _connectionMemory.Close();
                }
            }
            catch (Exception) { throw; }
        }
        /// <summary>
        /// 备份内存数据库到磁盘
        /// </summary>
        public void MemoryDatabaseToDisk()
        {
            try
            {
                if (_connectionMemory != null && _isThreadWriteDbRuning == false)
                {
                    if (_connectionDisk == null)
                    {
                        _connectionDisk = new SQLiteConnection(ConnectionString);
                    }
                    if (_connectionMemory.State != ConnectionState.Open)
                    {
                        _connectionMemory.Open();
                    }
                    if (_connectionDisk.State != ConnectionState.Open)
                    {
                        _connectionDisk.Open();
                    }
                    _connectionMemory.BackupDatabase(_connectionDisk, "main", "main", -1, null, -1);
                    _connectionDisk.Close();
                }
            }
            catch (Exception) { throw; }
        }

        #region 非查询sql队列写数据库线程
        /// <summary>
        /// 启动写数据库线程
        /// </summary>
        private void StartWriteDbThread()
        {
            if (!_isThreadWriteDbRuning)
            {
                _isThreadWriteDbRuning = true;
                _isClosingDB = false;
                _threadWriteDB = new Thread(WriteSqlToDBTask);
                _threadWriteDB.IsBackground = false;
                _threadWriteDB.Start();
            }
        }
        /// <summary>
        /// 停止写数据库线程
        /// </summary>
        private void StopWriteDbThread()
        {
            if(_isThreadWriteDbRuning)
            {
                _isThreadWriteDbRuning = false;
                _isClosingDB = true;
                _threadWriteDB.Join();
                _threadWriteDB = null;
            }
        }

        /// <summary>
        /// 写数据库线程
        /// </summary>
        private void WriteSqlToDBTask()
        {
            SQLiteTransaction trans;
            int cnt = 0;
            if (_connectionDisk == null)
            {
                _connectionDisk = new SQLiteConnection(ConnectionString);
            }
            if (_connectionDisk.State != ConnectionState.Open)
            {
                _connectionDisk.Open();
            }

            while (_isThreadWriteDbRuning || _noneQuerySqlQueue.Count > 0)
            {

                while (_noneQuerySqlQueue.Count < 20000 && _isClosingDB == false)
                {
                    Thread.Sleep(50);
                }

                trans = _connectionDisk.BeginTransaction();
                while (_noneQuerySqlQueue.Count > 0 && _noneQuerySqlQueue.TryDequeue(out cmd))
                {
                    cmd.Connection = _connectionDisk;
                    cmd.Transaction = null;
                    cmd.ExecuteNonQuery();

                    cmd.Dispose();

                    cmd = new SQLiteCommand();
                    _sqlCmdPool.Enqueue(cmd);

                    if (_noneQuerySqlQueue.Count == 0 || cnt++ >= 20000)
                    {
                        cnt = 0;
                        break;
                    }
                }
                trans.Commit();
                GC.Collect();
            }
            
            _connectionDisk.Close();
            _connectionDisk = null;
        }
        #endregion

        /// <summary>
        /// 执行SQL命令 - 增、删、改
        /// </summary>
        /// <param name="sqlText">SQL命令字符串</param>
        /// <param name="parameters">其他参数</param>
        /// <returns>影响的行数</returns>
        public int ExecuteNonQuery(string sqlText, params SQLiteParameter[] parameters)
        {
            int affectRows = 0;

            try
            {
                SQLiteConnection con = OpenConnection();
                SQLiteCommand cmd = new SQLiteCommand(sqlText, con);

                if (parameters != null && parameters.Length > 0) cmd.Parameters.AddRange(parameters);

                affectRows = cmd.ExecuteNonQuery();

                if (_connectionCurrent != _connectionDisk)
                {
#if true
                    SQLiteCommand newCmd;
                    if(_sqlCmdPool.TryDequeue(out newCmd) == false)
                    {
                        newCmd = (SQLiteCommand)cmd.Clone();
                    }
                    newCmd.CommandText = cmd.CommandText;
                    newCmd.Parameters.AddRange(parameters);
                    _noneQuerySqlQueue.Enqueue(newCmd);
#endif
                    //_noneQuerySqlQueue.Enqueue((SQLiteCommand)cmd.Clone());
                }
            }
            catch (Exception) { throw; }

            return affectRows;
        }
        /// <summary>
        /// 执行SQL命令 - 增、删、改
        /// </summary>
        /// <param name="cmd">已经设置好连接/命令字符串/参数/事务的命令</param>
        /// <returns></returns>
        public int ExecuteNonQuery(SQLiteCommand cmd)
        {
            int affectRows = 0;

            try
            {
                affectRows = cmd.ExecuteNonQuery();

                if (_connectionCurrent != _connectionDisk)
                {
#if true
                    SQLiteCommand newCmd;
                    if (_sqlCmdPool.TryDequeue(out newCmd) == false)
                    {
                        newCmd = (SQLiteCommand)cmd.Clone();
                    }
                    newCmd.CommandText = cmd.CommandText;
                    foreach (SQLiteParameter param in cmd.Parameters)
                    {
                        newCmd.Parameters.Add(param);
                    }
                    
                    _noneQuerySqlQueue.Enqueue(newCmd);
#endif
                    //_noneQuerySqlQueue.Enqueue((SQLiteCommand)cmd.Clone());
                }
            }
            catch (Exception) { throw; }

            return affectRows;
        }
        /// <summary>
        /// 执行批处理 - 增、删、改
        /// </summary>
        /// <param name="list">key = sqlText, value = parameters</param>
        public void ExecuteNonQueryBatch(List< KeyValuePair<string, SQLiteParameter[]>> list)
        {
            try
            {
                SQLiteConnection con = OpenConnection();
                SQLiteTransaction trans = con.BeginTransaction();
                SQLiteCommand cmd = new SQLiteCommand(con);

                foreach (var item in list)
                {
                    cmd.CommandText = item.Key;
                    if (item.Value != null) cmd.Parameters.AddRange(item.Value);
                    cmd.ExecuteNonQuery();

                    if (_connectionCurrent != _connectionDisk)
                    {
                        _noneQuerySqlQueue.Enqueue((SQLiteCommand)cmd.Clone());
                    }
                }
                trans.Commit();
            }
            catch (Exception) { throw; }
        }
        public void ExecuteNonQueryBatch(List<string> sqlTexts)
        {
            try
            {
                SQLiteConnection con = OpenConnection();
                SQLiteTransaction trans = con.BeginTransaction();
                SQLiteCommand cmd = new SQLiteCommand(con);

                foreach (var item in sqlTexts)
                {
                    cmd.CommandText = item;
                    cmd.ExecuteNonQuery();

                    if (_connectionCurrent != _connectionDisk)
                    {
                        _noneQuerySqlQueue.Enqueue((SQLiteCommand)cmd.Clone());
                    }
                }
                trans.Commit();
            }
            catch (Exception) { throw; }
        }

        /// <summary>
        /// 执行SQL命令 - 查询
        /// </summary>
        /// <returns>查询的结果</returns>
        /// <param name="sqlText">SQL命令字符串</param>
        /// <param name="parameters">其他参数</param>
        public SQLiteDataReader ExecuteReader(string sqlText, params SQLiteParameter[] parameters)
        {
            try
            {
                SQLiteConnection con = OpenConnection();
                SQLiteCommand cmd = new SQLiteCommand(sqlText, con);

                if (parameters != null && parameters.Length > 0) cmd.Parameters.AddRange(parameters);

                return cmd.ExecuteReader();
            }
            catch (Exception) { throw; }
        }

        public DataTable ExecuteReaderToDataTable(string sqlText, params SQLiteParameter[] parameters)
        {
            DataTable tb = new DataTable();
            
            try
            {
                SQLiteConnection con = OpenConnection();
                SQLiteCommand cmd = new SQLiteCommand(sqlText, con);

                if (parameters != null && parameters.Length > 0) cmd.Parameters.AddRange(parameters);

                SQLiteDataAdapter adt = new SQLiteDataAdapter(cmd);
                adt.Fill(tb);
            }
            catch (Exception) { throw; }

            return tb;
        }

        /// <summary>
        /// 执行SQL命令 - 检索
        /// </summary>
        /// <returns>检索的结果</returns>
        /// <param name="sqlText">SQL命令字符串</param>
        /// <param name="parameters">其他参数</param>
        public object ExecuteScalar(string sqlText, params SQLiteParameter[] parameters)
        {
            try
            {
                SQLiteConnection con = OpenConnection();
                SQLiteCommand cmd = new SQLiteCommand(sqlText, con);

                if (parameters != null && parameters.Length > 0) cmd.Parameters.AddRange(parameters);

                return cmd.ExecuteScalar();
            }
            catch (Exception) { throw; }
        }

        /// <summary>
        /// 读取整张数据表
        /// </summary>
        /// <returns>The full table.</returns>
        /// <param name="tableName">数据表名称</param>
        public SQLiteDataReader ReadFullTable(string tableName)
        {
            string queryString = "SELECT * FROM " + tableName;
            return ExecuteReader(queryString);
        }

        /// <summary>
        /// 向指定数据表中插入数据
        /// </summary>
        /// <returns>The values.</returns>
        /// <param name="tableName">数据表名称</param>
        /// <param name="values">插入的数值</param>
        public int InsertValues(string tableName, string[] values)
        {
            //获取数据表中字段数目
            SQLiteDataReader reader = ReadFullTable(tableName);
            int fieldCount = reader.FieldCount;
            reader.Close();

            //当插入的数据长度不等于字段数目时引发异常
            if (values.Length != fieldCount)
            {
                throw new SQLiteException("values.Length!=fieldCount");
            }

            string queryString = "INSERT INTO " + tableName + " VALUES (" + "'" + values[0] + "'";
            for (int i = 1; i < values.Length; i++)
            {
                queryString += ", " + "'" + values[i] + "'";
            }
            queryString += " )";
            return ExecuteNonQuery(queryString);
        }

        /// <summary>
        /// 更新指定数据表内的数据
        /// </summary>
        /// <returns>The values.</returns>
        /// <param name="tableName">数据表名称</param>
        /// <param name="colNames">字段名</param>
        /// <param name="colValues">字段名对应的数据</param>
        /// <param name="key">关键字</param>
        /// <param name="value">关键字对应的值</param>
        /// <param name="operation">运算符：=,<,>,...，默认“=”</param>
        public int UpdateValues(string tableName, string[] colNames, string[] colValues, string key, string value, string operation = "=")
        {
            //当字段名称和字段数值不对应时引发异常
            if (colNames.Length != colValues.Length)
            {
                throw new SQLiteException("colNames.Length!=colValues.Length");
            }

            string queryString = "UPDATE " + tableName + " SET " + colNames[0] + "=" + "'" + colValues[0] + "'";
            for (int i = 1; i < colValues.Length; i++)
            {
                queryString += ", " + colNames[i] + "=" + "'" + colValues[i] + "'";
            }
            queryString += " WHERE " + key + operation + "'" + value + "'";
            return ExecuteNonQuery(queryString);
        }

        /// <summary>
        /// 删除指定数据表内的数据
        /// </summary>
        /// <returns>The values.</returns>
        /// <param name="tableName">数据表名称</param>
        /// <param name="colNames">字段名</param>
        /// <param name="colValues">字段名对应的数据</param>
        public int DeleteValuesOR(string tableName, string[] colNames, string[] colValues, string[] operations)
        {
            //当字段名称和字段数值不对应时引发异常
            if (colNames.Length != colValues.Length || operations.Length != colNames.Length || operations.Length != colValues.Length)
            {
                throw new SQLiteException("colNames.Length!=colValues.Length || operations.Length!=colNames.Length || operations.Length!=colValues.Length");
            }

            string queryString = "DELETE FROM " + tableName + " WHERE " + colNames[0] + operations[0] + "'" + colValues[0] + "'";
            for (int i = 1; i < colValues.Length; i++)
            {
                queryString += "OR " + colNames[i] + operations[0] + "'" + colValues[i] + "'";
            }
            return ExecuteNonQuery(queryString);
        }

        /// <summary>
        /// 删除指定数据表内的数据
        /// </summary>
        /// <returns>The values.</returns>
        /// <param name="tableName">数据表名称</param>
        /// <param name="colNames">字段名</param>
        /// <param name="colValues">字段名对应的数据</param>
        public int DeleteValuesAND(string tableName, string[] colNames, string[] colValues, string[] operations)
        {
            //当字段名称和字段数值不对应时引发异常
            if (colNames.Length != colValues.Length || operations.Length != colNames.Length || operations.Length != colValues.Length)
            {
                throw new SQLiteException("colNames.Length!=colValues.Length || operations.Length!=colNames.Length || operations.Length!=colValues.Length");
            }

            string queryString = "DELETE FROM " + tableName + " WHERE " + colNames[0] + operations[0] + "'" + colValues[0] + "'";
            for (int i = 1; i < colValues.Length; i++)
            {
                queryString += " AND " + colNames[i] + operations[i] + "'" + colValues[i] + "'";
            }
            return ExecuteNonQuery(queryString);
        }


        /// <summary>
        /// 创建数据表
        /// </summary> +
        /// <returns>The table.</returns>
        /// <param name="tableName">数据表名</param>
        /// <param name="colNames">字段名</param>
        /// <param name="colTypes">字段名类型</param>
        public int CreateTable(string tableName, string[] colNames, string[] colTypes)
        {
            string queryString = "CREATE TABLE IF NOT EXISTS " + tableName + "( " + colNames[0] + " " + colTypes[0];
            for (int i = 1; i < colNames.Length; i++)
            {
                queryString += ", " + colNames[i] + " " + colTypes[i];
            }
            queryString += "  ) ";
            return ExecuteNonQuery(queryString);
        }

        /// <summary>
        /// 删除数据表
        /// </summary> +
        /// <returns>The table.</returns>
        /// <param name="tableName">数据表名</param>
        /// <param name="colNames">字段名</param>
        /// <param name="colTypes">字段名类型</param>
        public int DeleteTable(string tableName)
        {
            string queryString = "trop table " + tableName;

            return ExecuteNonQuery(queryString);
        }

        /// <summary>
        /// Reads the table.
        /// </summary>
        /// <returns>The table.</returns>
        /// <param name="tableName">Table name.</param>
        /// <param name="items">Items.</param>
        /// <param name="colNames">Col names.</param>
        /// <param name="operations">Operations.</param>
        /// <param name="colValues">Col values.</param>
        public SQLiteDataReader ReadTable(string tableName, string[] items, string[] colNames, string[] operations, string[] colValues)
        {
            string queryString = "SELECT " + items[0];
            for (int i = 1; i < items.Length; i++)
            {
                queryString += ", " + items[i];
            }
            queryString += " FROM " + tableName + " WHERE " + colNames[0] + " " + operations[0] + " " + colValues[0];
            for (int i = 0; i < colNames.Length; i++)
            {
                queryString += " AND " + colNames[i] + " " + operations[i] + " " + colValues[0] + " ";
            }
            return ExecuteReader(queryString);
        }

        /// <summary>
        /// 本类log
        /// </summary>
        /// <param name="s"></param>
        static void Log(string s)
        {
            Console.WriteLine("class SqLiteHelper:::" + s);
        }

    }
}
