﻿// -----------------------------------------------------------------------
// <copyright company="Fireasy"
//      email="faib920@126.com"
//      qq="55570729">
//   (c) Copyright Fireasy. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------
using Fireasy.Data.RecordWrapper;
using System;
using System.Data;

namespace Fireasy.Data
{
    /// <summary>
    /// 一个数据行的映射器，用于将数据行转换为对象。
    /// </summary>
    public interface IDataRowMapper
    {
        /// <summary>
        /// 将一个 <see cref="IDataReader"/> 转换为一个对象。
        /// </summary>
        /// <param name="database">当前的 <see cref="IDatabase"/> 对象。</param>
        /// <param name="reader">一个 <see cref="IDataReader"/> 对象。</param>
        /// <returns>由当前 <see cref="IDataReader"/> 对象中的数据转换成对象实例。</returns>
        object Map(IDatabase database, IDataReader reader);

        /// <summary>
        /// 获取或设置 <see cref="IRecordWrapper"/>。
        /// </summary>
        IRecordWrapper RecordWrapper { get; set; }

        /// <summary>
        /// 获取或设置对象的初始化器。
        /// </summary>
        Action<object> Initializer { get; set; }
    }

    /// <summary>
    /// 一个数据行的映射器，用于将数据行转换为 <typeparamref name="T"/> 类型的对象。
    /// </summary>
    /// <typeparam name="T">要转换的类型。</typeparam>
    public interface IDataRowMapper<out T> : IDataRowMapper
    {
        /// <summary>
        /// 将一个 <see cref="IDataReader"/> 转换为一个 <typeparamref name="T"/> 的对象。
        /// </summary>
        /// <param name="database">当前的 <see cref="IDatabase"/> 对象。</param>
        /// <param name="reader">一个 <see cref="IDataReader"/> 对象。</param>
        /// <returns>由当前 <see cref="IDataReader"/> 对象中的数据转换成的 <typeparamref name="T"/> 对象实例。</returns>
        new T Map(IDatabase database, IDataReader reader);
    }
}
