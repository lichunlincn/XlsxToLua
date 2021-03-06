﻿using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Text;

public class TableExportToMySQLHelper
{
    private static MySqlConnection _conn = null;
    private static string _schemaName = null;
    // 导出数据前获取的数据库中已存在的表名
    private static List<string> _existTableNames = new List<string>();

    // MySQL支持的用于定义Schema名的参数名
    private static string[] _DEFINE_SCHEMA_NAME_PARAM = { "Database", "Initial Catalog" };

    private const string _CREATE_TABLE_SQL = "CREATE TABLE {0} ( {1} PRIMARY KEY (`{2}`));";
    private const string _DROP_TABLE_SQL = "DROP TABLE {0};";
    private const string _INSERT_DATA_SQL = "INSERT INTO {0} ({1}) VALUES {2};";

    public static bool ConnectToDatabase(out string errorString)
    {
        if (AppValues.ConfigData.ContainsKey(AppValues.APP_CONFIG_MYSQL_CONNECT_STRING_KEY))
        {
            // 提取MySQL连接字符串中的Schema名
            string connectString = AppValues.ConfigData[AppValues.APP_CONFIG_MYSQL_CONNECT_STRING_KEY];
            foreach (string legalSchemaNameParam in _DEFINE_SCHEMA_NAME_PARAM)
            {
                int defineStartIndex = connectString.IndexOf(legalSchemaNameParam, StringComparison.CurrentCultureIgnoreCase);
                if (defineStartIndex != -1)
                {
                    // 查找后面的等号
                    int equalSignIndex = -1;
                    for (int i = defineStartIndex + legalSchemaNameParam.Length; i < connectString.Length; ++i)
                    {
                        if (connectString[i] == '=')
                        {
                            equalSignIndex = i;
                            break;
                        }
                    }
                    if (equalSignIndex == -1 || equalSignIndex + 1 == connectString.Length)
                    {
                        errorString = string.Format("MySQL数据库连接字符串（\"{0}\"）中\"{1}\"后需要跟\"=\"进行Schema名声明", connectString, legalSchemaNameParam);
                        return false;
                    }
                    else
                    {
                        // 查找定义的Schema名，在参数声明的=后面截止到下一个分号或字符串结束
                        int semicolonIndex = -1;
                        for (int i = equalSignIndex + 1; i < connectString.Length; ++i)
                        {
                            if (connectString[i] == ';')
                            {
                                semicolonIndex = i;
                                break;
                            }
                        }
                        if (semicolonIndex == -1)
                            _schemaName = connectString.Substring(equalSignIndex + 1).Trim();
                        else
                            _schemaName = connectString.Substring(equalSignIndex + 1, semicolonIndex - equalSignIndex - 1).Trim();
                    }

                    break;
                }
            }
            if (_schemaName == null)
            {
                errorString = string.Format("MySQL数据库连接字符串（\"{0}\"）中不包含Schema名的声明，请在{1}中任选一个参数名进行声明", connectString, Utils.CombineString(_DEFINE_SCHEMA_NAME_PARAM, ","));
                return false;
            }

            try
            {
                _conn = new MySqlConnection(connectString);
                _conn.Open();
                if (_conn.State == System.Data.ConnectionState.Open)
                {
                    // 获取已经存在的表格名
                    DataTable schemaInfo = _conn.GetSchema(SqlClientMetaDataCollectionNames.Tables);
                    foreach (DataRow info in schemaInfo.Rows)
                        _existTableNames.Add(info.ItemArray[2].ToString());

                    errorString = null;
                    return true;
                }
                else
                {
                    errorString = "未知错误";
                    return true;
                }
            }
            catch (MySqlException exception)
            {
                errorString = exception.Message;
                return false;
            }
        }
        else
        {
            errorString = string.Format("未在config配置文件中以名为\"{0}\"的key声明连接MySQL的字符串", AppValues.APP_CONFIG_MYSQL_CONNECT_STRING_KEY);
            return false;
        }
    }

    public static bool ExportTableToDatabase(TableInfo tableInfo, out string errorString)
    {
        Utils.Log(string.Format("导出表格\"{0}\"：", tableInfo.TableName));
        if (tableInfo.TableConfig != null && tableInfo.TableConfig.ContainsKey(AppValues.CONFIG_NAME_EXPORT_DATABASE_TABLE_NAME))
        {
            List<string> inputParams = tableInfo.TableConfig[AppValues.CONFIG_NAME_EXPORT_DATABASE_TABLE_NAME];
            if (inputParams == null || inputParams.Count < 1 || string.IsNullOrEmpty(inputParams[0]))
            {
                Utils.LogWarning("警告：未在表格配置中声明该表导出到数据库中的表名，此表将不被导出到数据库，请确认是否真要如此");
                errorString = null;
                return true;
            }
            string tableName = inputParams[0];
            // 检查数据库中是否已经存在同名表格，若存在删除旧表
            if (_existTableNames.Contains(tableName))
            {
                _DropTable(tableName, out errorString);
                if (!string.IsNullOrEmpty(errorString))
                {
                    errorString = string.Format("数据库中存在同名表格，但删除旧表失败，{0}", errorString);
                    return false;
                }
            }
            // 按Excel表格中字段定义新建数据库表格
            _CreateTable(tableName, tableInfo, out errorString);
            if (string.IsNullOrEmpty(errorString))
            {
                // 将Excel表格中的数据添加至数据库
                _InsertData(tableName, tableInfo, out errorString);
                if (string.IsNullOrEmpty(errorString))
                {
                    Utils.Log("成功");

                    errorString = null;
                    return true;
                }
                else
                {
                    errorString = string.Format("插入数据失败，{0}", errorString);
                    return false;
                }
            }
            else
            {
                errorString = string.Format("创建表格失败，{0}", errorString);
                return false;
            }
        }
        else
        {
            Utils.LogWarning("警告：未在表格配置中声明该表导出到数据库中的表名，此表将不被导出到数据库，请确认是否真要如此");
            errorString = null;
            return true;
        }
    }

    private static bool _InsertData(string tableName, TableInfo tableInfo, out string errorString)
    {
        List<FieldInfo> allDatabaseFieldInfo = GetAllDatabaseFieldInfo(tableInfo);

        // 生成所有字段名对应的定义字符串
        List<string> fileNames = new List<string>();
        foreach (FieldInfo fieldInfo in allDatabaseFieldInfo)
            fileNames.Add(string.Format("`{0}`", fieldInfo.DatabaseFieldName));

        string fieldNameDefineString = Utils.CombineString(fileNames, ", ");

        // 逐行生成插入数据的SQL语句中的value定义部分
        StringBuilder valueDefineStringBuilder = new StringBuilder();
        int count = tableInfo.GetKeyColumnFieldInfo().Data.Count;
        for (int i = 0; i < count; ++i)
        {
            List<string> values = new List<string>();
            foreach (FieldInfo fieldInfo in allDatabaseFieldInfo)
            {
                if (fieldInfo.Data[i] == null)
                    values.Add("NULL");
                else
                    values.Add(string.Format("'{0}'", fieldInfo.Data[i].ToString()));
            }
            valueDefineStringBuilder.AppendFormat("({0}),", Utils.CombineString(values, ","));
        }
        // 去掉末尾多余的逗号
        string valueDefineString = valueDefineStringBuilder.ToString();
        valueDefineString = valueDefineString.Substring(0, valueDefineString.Length - 1);

        string insertSqlString = string.Format(_INSERT_DATA_SQL, _CombineDatabaseTableFullName(tableName), fieldNameDefineString, valueDefineString);

        // 执行插入操作
        try
        {
            MySqlCommand cmd = new MySqlCommand(insertSqlString, _conn);
            int insertCount = cmd.ExecuteNonQuery();
            if (insertCount < count)
            {
                errorString = string.Format("需要插入{0}条数据但仅插入了{1}条");
                return false;
            }
            else
            {
                errorString = null;
                return true;
            }
        }
        catch (MySqlException exception)
        {
            errorString = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// 获取某张表格中对应要导出到数据库的字段集合
    /// </summary>
    public static List<FieldInfo> GetAllDatabaseFieldInfo(TableInfo tableInfo)
    {
        List<FieldInfo> allFieldInfo = new List<FieldInfo>();
        foreach (FieldInfo fieldInfo in tableInfo.GetAllFieldInfo())
            _GetOneDatabaseFieldInfo(fieldInfo, allFieldInfo);

        return allFieldInfo;
    }

    private static void _GetOneDatabaseFieldInfo(FieldInfo fieldInfo, List<FieldInfo> allFieldInfo)
    {
        if (fieldInfo.DataType == DataType.Array || fieldInfo.DataType == DataType.Dict)
        {
            foreach (FieldInfo childFieldInfo in fieldInfo.ChildField)
                _GetOneDatabaseFieldInfo(childFieldInfo, allFieldInfo);
        }
        else
            allFieldInfo.Add(fieldInfo);
    }

    /// <summary>
    /// 将数据库的表名连同Schema名组成形如'SchemaName'.'tableName'的字符串
    /// </summary>
    private static string _CombineDatabaseTableFullName(string tableName)
    {
        return string.Format("`{0}`.`{1}`", _schemaName, tableName);
    }

    private static bool _CreateTable(string tableName, TableInfo tableInfo, out string errorString)
    {
        // 生成在创建数据表时所有字段的声明
        StringBuilder fieldDefineStringBuilder = new StringBuilder();
        foreach (FieldInfo fieldInfo in GetAllDatabaseFieldInfo(tableInfo))
            fieldDefineStringBuilder.AppendFormat("`{0}` {1} COMMENT '{2}',", fieldInfo.DatabaseFieldName, fieldInfo.DatabaseFieldType, fieldInfo.Desc);

        string createTableSql = string.Format(_CREATE_TABLE_SQL, _CombineDatabaseTableFullName(tableName), fieldDefineStringBuilder.ToString(), tableInfo.GetKeyColumnFieldInfo().DatabaseFieldName);

        try
        {
            MySqlCommand cmd = new MySqlCommand(createTableSql, _conn);
            cmd.ExecuteNonQuery();
            errorString = null;
            return true;
        }
        catch (MySqlException exception)
        {
            errorString = exception.Message;
            return false;
        }
    }

    private static bool _DropTable(string tableName, out string errorString)
    {
        try
        {
            MySqlCommand cmd = new MySqlCommand(string.Format(_DROP_TABLE_SQL, _CombineDatabaseTableFullName(tableName)), _conn);
            cmd.ExecuteNonQuery();
            errorString = null;
            return true;
        }
        catch (MySqlException exception)
        {
            errorString = exception.Message;
            return false;
        }
    }
}
