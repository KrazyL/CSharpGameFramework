﻿using System;
using System.Reflection;
using System.Diagnostics;
using System.Collections.Generic;
using GameFramework;
using GameFramework.DataCache;
using System.Threading;
using System.Text;
using Messenger;
using GameFrameworkMessage;
using GameFrameworkData;

internal class DataCacheSystem : MyServerThread
{
    internal GameFramework.ServerAsyncActionProcessor LoadActionQueue
    {
        get { return m_LoadActionQueue; }
    }
    internal GameFramework.ServerAsyncActionProcessor SaveActionQueue
    {
        get { return m_SaveActionQueue; }
    }
    //==================================================================
    internal void Init()
    {
        PersistentSystem.Instance.Init();
        Start();
        LogSys.Log(LOG_TYPE.INFO, "DataCacheSystem initialized");
    }
    //==========================通过QueueAction调用的方法===========================================
    //注意!回调函数目前在缓存线程与db线程都可能调用，回调函数的实现需要是线程安全的(目前一般都是发消息，满足此条件)。
    internal void Load(Msg_LD_Load msg, PBChannel channel, int handle)
    {
        //首先在缓存中查找数据,若未找到,则到DB中查找  
        bool isLoadCache = true;
        Msg_DL_LoadResult ret = new Msg_DL_LoadResult();
        ret.MsgId = msg.MsgId;
        ret.PrimaryKeys.AddRange(msg.PrimaryKeys);
        ret.SerialNo = msg.SerialNo;
        ret.ErrorNo = 0;
        for (int i = 0; i < msg.LoadRequests.Count; ++i) {
            Msg_LD_SingleLoadRequest req = msg.LoadRequests[i];
            KeyString loadKey = KeyString.Wrap(req.Keys);
            switch (req.LoadType) {
                case Msg_LD_SingleLoadRequest.LoadTypeEnum.LoadAll: {
                        isLoadCache = false;
                    }
                    break;
                case Msg_LD_SingleLoadRequest.LoadTypeEnum.LoadSingle: {
                        InnerCacheItem item = m_InnerCacheSystem.Find(req.MsgId, loadKey);
                        if (item != null) {
                            Msg_DL_SingleRowResult result = new Msg_DL_SingleRowResult();
                            result.MsgId = req.MsgId;
                            result.PrimaryKeys.AddRange(req.Keys);
                            result.DataVersion = 0;         //TODO: 这个DataVersion有用吗?
                            result.Data = item.DataMessage;
                            ret.Results.Add(result);
                        } else {
                            isLoadCache = false;
                        }
                    }
                    break;
                case Msg_LD_SingleLoadRequest.LoadTypeEnum.LoadMulti: {
                        List<InnerCacheItem> itemList = m_InnerCacheSystem.FindByForeignKey(req.MsgId, loadKey);
                        foreach (var item in itemList) {
                            Msg_DL_SingleRowResult result = new Msg_DL_SingleRowResult();
                            result.MsgId = req.MsgId;
                            result.PrimaryKeys.AddRange(req.Keys);
                            result.DataVersion = 0;         //TODO: 这个DataVersion有用吗?
                            result.Data = item.DataMessage;
                            ret.Results.Add(result);
                        }
                    }
                    break;
            }
        }
        if (isLoadCache) {
            channel.Send(ret);
            LogSys.Log(LOG_TYPE.INFO, "Load data from cache. MsgId:{0}, Key:{1}", msg.MsgId, KeyString.Wrap(msg.PrimaryKeys).ToString());
        } else {
            //查找DB交给DBLoad线程操作
            DbThreadManager.Instance.LoadActionQueue.QueueAction(DataLoadImplement.Load, msg, (MyAction<Msg_DL_LoadResult>)((Msg_DL_LoadResult result) => {
                if (result.ErrorNo == Msg_DL_LoadResult.ErrorNoEnum.Success) {
                    foreach (Msg_DL_SingleRowResult row in result.Results) {
                        m_InnerCacheSystem.AddOrUpdate(row.MsgId, KeyString.Wrap(row.PrimaryKeys), KeyString.Wrap(row.ForeignKeys), row.Data);
                    }
                }
                channel.Send(result);
                LogSys.Log(LOG_TYPE.INFO, "Load data from database. MsgId:{0}, Key:{1}", msg.MsgId, KeyString.Wrap(msg.PrimaryKeys).ToString());
            }));
        }
    }
    internal void Save(int msgId, List<string> primaryKey, List<string> foreignKey, byte[] dataBytes, long serialNo)
    {
        //更新缓存
        m_InnerCacheSystem.AddOrUpdate(msgId, KeyString.Wrap(primaryKey), KeyString.Wrap(foreignKey), dataBytes, serialNo);
    }
    internal void DoLastSave()
    {
        PersistentSystem.Instance.LastSaveToDB();
    }
    //==========================只能在本线程调用的方法===========================================
    internal Dictionary<int, List<InnerCacheItem>> FetchDirtyCacheItems()
    {
        return m_InnerCacheSystem.FetchDirtyCacheItems();
    }
    //=====================================================================
    protected override void OnStart()
    {
        TickSleepTime = 10;
        ActionNumPerTick = 8192;
    }
    protected override void OnTick()
    {
        try {
            long curTime = TimeUtility.GetLocalMilliseconds();
            if (m_LastTickTime != 0) {
                long elapsedTickTime = curTime - m_LastTickTime;
                if (elapsedTickTime > c_WarningTickTime) {
                    LogSys.Log(LOG_TYPE.MONITOR, "DataCacheSystem Tick:{0}", elapsedTickTime);
                }
            }
            m_LastTickTime = curTime;

            if (m_LastLogTime + 60000 < curTime) {
                m_LastLogTime = curTime;
                DebugPoolCount((string msg) => {
                    LogSys.Log(LOG_TYPE.INFO, "DataCacheSystem.ActionQueue {0}", msg);
                });
                m_LoadActionQueue.DebugPoolCount((string msg) => {
                    LogSys.Log(LOG_TYPE.INFO, "DataCacheSystem.LoadActionQueue {0}", msg);
                });
                m_SaveActionQueue.DebugPoolCount((string msg) => {
                    LogSys.Log(LOG_TYPE.INFO, "DataCacheSystem.SaveActionQueue {0}", msg);
                });
                LogSys.Log(LOG_TYPE.MONITOR, "DataCacheSystem.ActionQueue Current Action {0}", this.CurActionNum);
            }

            if (curTime - m_LastCacheTickTime > c_CacheTickInterval) {
                m_InnerCacheSystem.Tick();
                m_LastCacheTickTime = curTime;
            }

            m_LoadActionQueue.HandleActions(4096);
            m_SaveActionQueue.HandleActions(4096);
            PersistentSystem.Instance.Tick();
        } catch (Exception ex) {
            LogSys.Log(LOG_TYPE.ERROR, "DataCacheSystem ERROR:{0} \n StackTrace:{1}", ex.Message, ex.StackTrace);
            if (ex.InnerException != null) {
                LogSys.Log(LOG_TYPE.ERROR, "DataCacheSystem INNER ERROR:{0} \n StackTrace:{1}", ex.InnerException.Message, ex.InnerException.StackTrace);
            }
        }
    }

    private ServerAsyncActionProcessor m_LoadActionQueue = new ServerAsyncActionProcessor();
    private ServerAsyncActionProcessor m_SaveActionQueue = new ServerAsyncActionProcessor();
    private InnerCacheSystem m_InnerCacheSystem = new InnerCacheSystem();
    private long m_LastCacheTickTime = 0;
    private const long c_CacheTickInterval = 60000;     //InnerCache的Tick周期:10min
    private long m_LastLogTime = 0;
    private const long c_WarningTickTime = 1000;
    private long m_LastTickTime = 0;

    internal static DataCacheSystem Instance
    {
        get { return s_Instance; }
    }
    private static DataCacheSystem s_Instance = new DataCacheSystem();
}
