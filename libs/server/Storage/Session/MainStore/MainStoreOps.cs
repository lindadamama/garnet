﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Garnet.common;
using Tsavorite.core;

namespace Garnet.server
{
    using MainStoreAllocator = SpanByteAllocator<StoreFunctions<SpanByte, SpanByte, SpanByteComparer, SpanByteRecordDisposer>>;
    using MainStoreFunctions = StoreFunctions<SpanByte, SpanByte, SpanByteComparer, SpanByteRecordDisposer>;

    using ObjectStoreAllocator = GenericAllocator<byte[], IGarnetObject, StoreFunctions<byte[], IGarnetObject, ByteArrayKeyComparer, DefaultRecordDisposer<byte[], IGarnetObject>>>;
    using ObjectStoreFunctions = StoreFunctions<byte[], IGarnetObject, ByteArrayKeyComparer, DefaultRecordDisposer<byte[], IGarnetObject>>;

    sealed partial class StorageSession : IDisposable
    {
        public GarnetStatus GET<TContext>(ref SpanByte key, ref SpanByte input, ref SpanByteAndMemory output, ref TContext context)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
        {
            long ctx = default;
            var status = context.Read(ref key, ref input, ref output, ctx);

            if (status.IsPending)
            {
                StartPendingMetrics();
                CompletePendingForSession(ref status, ref output, ref context);
                StopPendingMetrics();
            }

            if (status.Found)
            {
                incr_session_found();
                return GarnetStatus.OK;
            }
            else
            {
                incr_session_notfound();
                return GarnetStatus.NOTFOUND;
            }
        }

        public unsafe GarnetStatus ReadWithUnsafeContext<TContext>(ArgSlice key, ref SpanByte input, ref SpanByteAndMemory output, long localHeadAddress, out bool epochChanged, ref TContext context)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>, IUnsafeContext
        {

            var _key = key.SpanByte;
            long ctx = default;

            epochChanged = false;
            var status = context.Read(ref _key, ref Unsafe.AsRef(in input), ref output, ctx);

            if (status.IsPending)
            {
                context.EndUnsafe();
                StartPendingMetrics();
                CompletePendingForSession(ref status, ref output, ref context);
                StopPendingMetrics();
                context.BeginUnsafe();
                // Start read of pointers from beginning if epoch changed
                if (HeadAddress == localHeadAddress)
                {
                    context.EndUnsafe();
                    epochChanged = true;
                }
            }
            else if (status.NotFound)
            {
                incr_session_notfound();
                return GarnetStatus.NOTFOUND;
            }
            else
            {
                incr_session_found();
            }

            return GarnetStatus.OK;
        }

        public unsafe GarnetStatus GET<TContext>(ArgSlice key, out ArgSlice value, ref TContext context)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
        {
            int inputSize = sizeof(int) + RespInputHeader.Size;

            byte* pbCmdInput = stackalloc byte[inputSize];
            byte* pcurr = pbCmdInput;
            *(int*)pcurr = inputSize - sizeof(int);
            pcurr += sizeof(int);
            (*(RespInputHeader*)pcurr).cmd = RespCommand.GET; // Proxy for "get value without RESP header"
            (*(RespInputHeader*)pcurr).flags = 0;

            var _key = key.SpanByte;
            var _output = new SpanByteAndMemory { SpanByte = scratchBufferManager.ViewRemainingArgSlice().SpanByte };
            var ret = GET(ref _key, ref Unsafe.AsRef<SpanByte>(pbCmdInput), ref _output, ref context);
            value = default;
            if (ret == GarnetStatus.OK)
            {
                if (!_output.IsSpanByte)
                {
                    value = scratchBufferManager.FormatScratch(0, _output.AsReadOnlySpan());
                    _output.Memory.Dispose();
                }
                else
                {
                    value = scratchBufferManager.CreateArgSlice(_output.Length);
                }
            }
            return ret;
        }

        public unsafe GarnetStatus GET<TContext>(ArgSlice key, out MemoryResult<byte> value, ref TContext context)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
        {
            int inputSize = sizeof(int) + RespInputHeader.Size;

            byte* pbCmdInput = stackalloc byte[inputSize];
            byte* pcurr = pbCmdInput;
            *(int*)pcurr = inputSize - sizeof(int);
            pcurr += sizeof(int);
            (*(RespInputHeader*)pcurr).cmd = RespCommand.GET; // Proxy for "get value without RESP header"
            (*(RespInputHeader*)pcurr).flags = 0;

            var _key = key.SpanByte;
            var _output = new SpanByteAndMemory();
            var ret = GET(ref _key, ref Unsafe.AsRef<SpanByte>(pbCmdInput), ref _output, ref context);
            value = new MemoryResult<byte>(_output.Memory, _output.Length);
            return ret;
        }

        public GarnetStatus GET<TObjectContext>(byte[] key, out GarnetObjectStoreOutput output, ref TObjectContext objectContext)
            where TObjectContext : ITsavoriteContext<byte[], IGarnetObject, ObjectInput, GarnetObjectStoreOutput, long, ObjectSessionFunctions, ObjectStoreFunctions, ObjectStoreAllocator>
        {
            long ctx = default;
            var status = objectContext.Read(key, out output, ctx);

            if (status.IsPending)
            {
                StartPendingMetrics();
                CompletePendingForObjectStoreSession(ref status, ref output, ref objectContext);
                StopPendingMetrics();
            }

            if (status.Found)
            {
                incr_session_found();
                return GarnetStatus.OK;
            }
            else
            {
                incr_session_notfound();
                return GarnetStatus.NOTFOUND;
            }
        }

        public unsafe GarnetStatus GETEX<TContext>(ref SpanByte key, TimeSpan? expiry, ref SpanByteAndMemory output, ref TContext context)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
        {
            long ctx = default;
            var hasExpiry = expiry.HasValue == true && expiry.Value.Ticks != 0;
            var pbCmdInput = stackalloc byte[sizeof(int) + sizeof(long) + RespInputHeader.Size];
            *(int*)pbCmdInput = sizeof(long) + RespInputHeader.Size;
            byte* pbCmdInputHeader = hasExpiry ? pbCmdInput + sizeof(int) + sizeof(long) : pbCmdInput + sizeof(int);
            ((RespInputHeader*)pbCmdInputHeader)->cmd = RespCommand.GETEX;
            ((RespInputHeader*)pbCmdInputHeader)->flags = 0;

            if (!hasExpiry)
            {
                // Reusing `ExtraMetadata` space when there is no expiry
                *(bool*)(pbCmdInputHeader + RespInputHeader.Size) = expiry.HasValue; // true to indicate persist option and false to indicate no expiry option
            }

            ref var input = ref SpanByte.Reinterpret(pbCmdInput);
            input.ExtraMetadata = hasExpiry ? DateTimeOffset.UtcNow.Ticks + expiry.Value.Ticks : 0;

            var status = context.RMW(ref key, ref input, ref output, ctx);

            if (status.IsPending)
            {
                StartPendingMetrics();
                CompletePendingForSession(ref status, ref output, ref context);
                StopPendingMetrics();
            }

            if (status.Found)
            {
                incr_session_found();
                return GarnetStatus.OK;
            }
            else
            {
                incr_session_notfound();
                return GarnetStatus.NOTFOUND;
            }
        }

        /// <summary>
        /// GETDEL command - Gets the value corresponding to the given key and deletes the key.
        /// </summary>
        /// <param name="key">The key to get the value for.</param>
        /// <param name="output">Span to allocate the output of the operation</param>
        /// <param name="context">Basic Context of the store</param>
        /// <returns> Operation status </returns>
        public unsafe GarnetStatus GETDEL<TContext>(ArgSlice key, ref SpanByteAndMemory output, ref TContext context)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
        {
            var _key = key.SpanByte;
            return GETDEL(ref _key, ref output, ref context);
        }

        /// <summary>
        /// GETDEL command - Gets the value corresponding to the given key and deletes the key.
        /// </summary>
        /// <param name="key">The key to get the value for.</param>
        /// <param name="output">Span to allocate the output of the operation</param>
        /// <param name="context">Basic Context of the store</param>
        /// <returns> Operation status </returns>
        public unsafe GarnetStatus GETDEL<TContext>(ref SpanByte key, ref SpanByteAndMemory output, ref TContext context)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
        {
            // size data + header
            int inputSize = sizeof(int) + RespInputHeader.Size;
            byte* pbCmdInput = stackalloc byte[inputSize];

            byte* pcurr = pbCmdInput;
            *(int*)pbCmdInput = inputSize - sizeof(int);
            pcurr += sizeof(int);
            ((RespInputHeader*)pcurr)->cmd = RespCommand.GETDEL;
            ((RespInputHeader*)pcurr)->flags = 0;

            ref var input = ref SpanByte.Reinterpret(pbCmdInput);

            var status = context.RMW(ref key, ref input, ref output);
            Debug.Assert(output.IsSpanByte);

            if (status.IsPending)
                CompletePendingForSession(ref status, ref output, ref context);

            return status.Found ? GarnetStatus.OK : GarnetStatus.NOTFOUND;
        }

        public unsafe GarnetStatus GETRANGE<TContext>(ref SpanByte key, int sliceStart, int sliceLength, ref SpanByteAndMemory output, ref TContext context)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
        {
            int inputSize = sizeof(int) + RespInputHeader.Size + sizeof(int) * 2;
            byte* pbCmdInput = stackalloc byte[inputSize];

            byte* pcurr = pbCmdInput;
            *(int*)pcurr = inputSize - sizeof(int);
            pcurr += sizeof(int);
            (*(RespInputHeader*)(pcurr)).cmd = RespCommand.GETRANGE;
            (*(RespInputHeader*)(pcurr)).flags = 0;
            pcurr += RespInputHeader.Size;
            *(int*)(pcurr) = sliceStart;
            *(int*)(pcurr + 4) = sliceLength;

            var status = context.Read(ref key, ref Unsafe.AsRef<SpanByte>(pbCmdInput), ref output);

            if (status.IsPending)
            {
                StartPendingMetrics();
                CompletePendingForSession(ref status, ref output, ref context);
                StopPendingMetrics();
            }

            if (status.Found)
            {
                incr_session_found();
                return GarnetStatus.OK;
            }
            else
            {
                incr_session_notfound();
                return GarnetStatus.NOTFOUND;
            }
        }


        /// <summary>
        /// Returns the remaining time to live of a key that has a timeout.
        /// </summary>
        /// <typeparam name="TContext"></typeparam>
        /// <typeparam name="TObjectContext"></typeparam>
        /// <param name="key">The key to get the remaining time to live in the store.</param>
        /// <param name="storeType">The store to operate on</param>
        /// <param name="output">Span to allocate the output of the operation</param>
        /// <param name="context">Basic Context of the store</param>
        /// <param name="objectContext">Object Context of the store</param>
        /// <param name="milliseconds">when true the command to execute is PTTL.</param>
        /// <returns></returns>
        public unsafe GarnetStatus TTL<TContext, TObjectContext>(ref SpanByte key, StoreType storeType, ref SpanByteAndMemory output, ref TContext context, ref TObjectContext objectContext, bool milliseconds = false)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
            where TObjectContext : ITsavoriteContext<byte[], IGarnetObject, ObjectInput, GarnetObjectStoreOutput, long, ObjectSessionFunctions, ObjectStoreFunctions, ObjectStoreAllocator>
        {
            int inputSize = sizeof(int) + RespInputHeader.Size;
            byte* pbCmdInput = stackalloc byte[inputSize];

            byte* pcurr = pbCmdInput;
            *(int*)pcurr = inputSize - sizeof(int);
            pcurr += sizeof(int);
            (*(RespInputHeader*)pcurr).cmd = milliseconds ? RespCommand.PTTL : RespCommand.TTL;
            (*(RespInputHeader*)pcurr).flags = 0;

            if (storeType == StoreType.Main || storeType == StoreType.All)
            {
                var status = context.Read(ref key, ref Unsafe.AsRef<SpanByte>(pbCmdInput), ref output);

                if (status.IsPending)
                {
                    StartPendingMetrics();
                    CompletePendingForSession(ref status, ref output, ref context);
                    StopPendingMetrics();
                }

                if (status.Found) return GarnetStatus.OK;
            }

            if ((storeType == StoreType.Object || storeType == StoreType.All) && !objectStoreBasicContext.IsNull)
            {
                var objInput = new ObjectInput
                {
                    header = new RespInputHeader
                    {
                        cmd = milliseconds ? RespCommand.PTTL : RespCommand.TTL,
                        type = milliseconds ? GarnetObjectType.PTtl : GarnetObjectType.Ttl,
                    },
                };

                var keyBA = key.ToByteArray();
                var objO = new GarnetObjectStoreOutput { spanByteAndMemory = output };
                var status = objectContext.Read(ref keyBA, ref objInput, ref objO);

                if (status.IsPending)
                    CompletePendingForObjectStoreSession(ref status, ref objO, ref objectContext);

                if (status.Found)
                {
                    output = objO.spanByteAndMemory;
                    return GarnetStatus.OK;
                }
            }
            return GarnetStatus.NOTFOUND;
        }

        /// <summary>
        /// Get the absolute Unix timestamp at which the given key will expire.
        /// </summary>
        /// <typeparam name="TContext"></typeparam>
        /// <typeparam name="TObjectContext"></typeparam>
        /// <param name="key">The key to get the Unix timestamp.</param>
        /// <param name="storeType">The store to operate on</param>
        /// <param name="output">Span to allocate the output of the operation</param>
        /// <param name="context">Basic Context of the store</param>
        /// <param name="objectContext">Object Context of the store</param>
        /// <param name="milliseconds">when true the command to execute is PEXPIRETIME.</param>
        /// <returns>Returns the absolute Unix timestamp (since January 1, 1970) in seconds or milliseconds at which the given key will expire.</returns>
        public unsafe GarnetStatus EXPIRETIME<TContext, TObjectContext>(ref SpanByte key, StoreType storeType, ref SpanByteAndMemory output, ref TContext context, ref TObjectContext objectContext, bool milliseconds = false)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
            where TObjectContext : ITsavoriteContext<byte[], IGarnetObject, ObjectInput, GarnetObjectStoreOutput, long, ObjectSessionFunctions, ObjectStoreFunctions, ObjectStoreAllocator>
        {
            int inputSize = sizeof(int) + RespInputHeader.Size;
            byte* pbCmdInput = stackalloc byte[inputSize];

            byte* pcurr = pbCmdInput;
            *(int*)pcurr = inputSize - sizeof(int);
            pcurr += sizeof(int);
            (*(RespInputHeader*)pcurr).cmd = milliseconds ? RespCommand.PEXPIRETIME : RespCommand.EXPIRETIME;
            (*(RespInputHeader*)pcurr).flags = 0;

            if (storeType == StoreType.Main || storeType == StoreType.All)
            {
                var status = context.Read(ref key, ref Unsafe.AsRef<SpanByte>(pbCmdInput), ref output);

                if (status.IsPending)
                {
                    StartPendingMetrics();
                    CompletePendingForSession(ref status, ref output, ref context);
                    StopPendingMetrics();
                }

                if (status.Found) return GarnetStatus.OK;
            }

            if ((storeType == StoreType.Object || storeType == StoreType.All) && !objectStoreBasicContext.IsNull)
            {
                var objInput = new ObjectInput
                {
                    header = new RespInputHeader
                    {
                        type = milliseconds ? GarnetObjectType.PExpiretime : GarnetObjectType.Expiretime,
                    },
                };

                var keyBA = key.ToByteArray();
                var objO = new GarnetObjectStoreOutput { spanByteAndMemory = output };
                var status = objectContext.Read(ref keyBA, ref objInput, ref objO);

                if (status.IsPending)
                    CompletePendingForObjectStoreSession(ref status, ref objO, ref objectContext);

                if (status.Found)
                {
                    output = objO.spanByteAndMemory;
                    return GarnetStatus.OK;
                }
            }
            return GarnetStatus.NOTFOUND;
        }

        public GarnetStatus SET<TContext>(ref SpanByte key, ref SpanByte value, ref TContext context)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
        {
            context.Upsert(ref key, ref value);
            return GarnetStatus.OK;
        }

        public unsafe GarnetStatus SET_Conditional<TContext>(ref SpanByte key, ref SpanByte input, ref TContext context)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
        {
            byte* pbOutput = stackalloc byte[8];
            var o = new SpanByteAndMemory(pbOutput, 8);

            var status = context.RMW(ref key, ref input, ref o);

            if (status.IsPending)
            {
                StartPendingMetrics();
                CompletePendingForSession(ref status, ref o, ref context);
                StopPendingMetrics();
            }

            if (status.NotFound)
            {
                incr_session_notfound();
                return GarnetStatus.NOTFOUND;
            }
            else
            {
                incr_session_found();
                return GarnetStatus.OK;
            }
        }

        public unsafe GarnetStatus SET_Conditional<TContext>(ref SpanByte key, ref SpanByte input, ref SpanByteAndMemory output, ref TContext context)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
        {
            var status = context.RMW(ref key, ref input, ref output);

            if (status.IsPending)
            {
                StartPendingMetrics();
                CompletePendingForSession(ref status, ref output, ref context);
                StopPendingMetrics();
            }

            if (status.NotFound)
            {
                incr_session_notfound();
                return GarnetStatus.NOTFOUND;
            }
            else
            {
                incr_session_found();
                return GarnetStatus.OK;
            }
        }

        public GarnetStatus SET<TContext>(ArgSlice key, ArgSlice value, ref TContext context)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
        {
            var _key = key.SpanByte;
            var _value = value.SpanByte;
            return SET(ref _key, ref _value, ref context);
        }

        public GarnetStatus SET<TObjectContext>(byte[] key, IGarnetObject value, ref TObjectContext objectContext)
            where TObjectContext : ITsavoriteContext<byte[], IGarnetObject, ObjectInput, GarnetObjectStoreOutput, long, ObjectSessionFunctions, ObjectStoreFunctions, ObjectStoreAllocator>
        {
            objectContext.Upsert(key, value);
            return GarnetStatus.OK;
        }

        public GarnetStatus SET<TContext>(ArgSlice key, Memory<byte> value, ref TContext context)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
        {
            var _key = key.SpanByte;
            unsafe
            {
                fixed (byte* ptr = value.Span)
                {
                    var _value = SpanByte.FromPinnedPointer(ptr, value.Length);
                    return SET(ref _key, ref _value, ref context);
                }
            }
        }

        public unsafe GarnetStatus SETEX<TContext>(ArgSlice key, ArgSlice value, ArgSlice expiryMs, ref TContext context)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
            => SETEX(key, value, TimeSpan.FromMilliseconds(NumUtils.BytesToLong(expiryMs.Length, expiryMs.ptr)), ref context);

        public GarnetStatus SETEX<TContext>(ArgSlice key, ArgSlice value, TimeSpan expiry, ref TContext context)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
        {
            var _key = key.SpanByte;
            var valueSB = scratchBufferManager.FormatScratch(sizeof(long), value).SpanByte;
            valueSB.ExtraMetadata = DateTimeOffset.UtcNow.Ticks + expiry.Ticks;
            return SET(ref _key, ref valueSB, ref context);
        }

        /// <summary>
        /// APPEND command - appends value at the end of existing string
        /// </summary>
        /// <typeparam name="TContext">Context type</typeparam>
        /// <param name="key">Key whose value is to be appended</param>
        /// <param name="value">Value to be appended</param>
        /// <param name="output">Length of updated value</param>
        /// <param name="context">Store context</param>
        /// <returns>Operation status</returns>
        public unsafe GarnetStatus APPEND<TContext>(ArgSlice key, ArgSlice value, ref ArgSlice output, ref TContext context)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
        {
            var _key = key.SpanByte;
            var _value = value.SpanByte;
            var _output = new SpanByteAndMemory(output.SpanByte);

            return APPEND(ref _key, ref _value, ref _output, ref context);
        }

        /// <summary>
        /// APPEND command - appends value at the end of existing string
        /// </summary>
        /// <typeparam name="TContext">Context type</typeparam>
        /// <param name="key">Key whose value is to be appended</param>
        /// <param name="value">Value to be appended</param>
        /// <param name="output">Length of updated value</param>
        /// <param name="context">Store context</param>
        /// <returns>Operation status</returns>
        public unsafe GarnetStatus APPEND<TContext>(ref SpanByte key, ref SpanByte value, ref SpanByteAndMemory output, ref TContext context)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
        {
            int inputSize = sizeof(int) + RespInputHeader.Size + sizeof(int) + sizeof(long);
            byte* pbCmdInput = stackalloc byte[inputSize];

            byte* pcurr = pbCmdInput;
            *(int*)pcurr = inputSize - sizeof(int);
            pcurr += sizeof(int);
            (*(RespInputHeader*)(pcurr)).cmd = RespCommand.APPEND;
            (*(RespInputHeader*)(pcurr)).flags = 0;

            pcurr += RespInputHeader.Size;
            *(int*)pcurr = value.Length;
            pcurr += sizeof(int);
            *(long*)pcurr = (long)value.ToPointer();

            var status = context.RMW(ref key, ref Unsafe.AsRef<SpanByte>(pbCmdInput), ref output);
            if (status.IsPending)
            {
                StartPendingMetrics();
                CompletePendingForSession(ref status, ref output, ref context);
                StopPendingMetrics();
            }

            Debug.Assert(output.IsSpanByte);

            return GarnetStatus.OK;
        }

        public GarnetStatus DELETE<TContext, TObjectContext>(ArgSlice key, StoreType storeType, ref TContext context, ref TObjectContext objectContext)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
            where TObjectContext : ITsavoriteContext<byte[], IGarnetObject, ObjectInput, GarnetObjectStoreOutput, long, ObjectSessionFunctions, ObjectStoreFunctions, ObjectStoreAllocator>
        {
            var _key = key.SpanByte;
            return DELETE(ref _key, storeType, ref context, ref objectContext);
        }

        public GarnetStatus DELETE<TContext, TObjectContext>(ref SpanByte key, StoreType storeType, ref TContext context, ref TObjectContext objectContext)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
            where TObjectContext : ITsavoriteContext<byte[], IGarnetObject, ObjectInput, GarnetObjectStoreOutput, long, ObjectSessionFunctions, ObjectStoreFunctions, ObjectStoreAllocator>
        {
            var found = false;

            if (storeType == StoreType.Main || storeType == StoreType.All)
            {
                var status = context.Delete(ref key);
                Debug.Assert(!status.IsPending);
                if (status.Found) found = true;
            }

            if (!objectStoreBasicContext.IsNull && (storeType == StoreType.Object || storeType == StoreType.All))
            {
                var keyBA = key.ToByteArray();
                var status = objectContext.Delete(ref keyBA);
                Debug.Assert(!status.IsPending);
                if (status.Found) found = true;
            }
            return found ? GarnetStatus.OK : GarnetStatus.NOTFOUND;
        }

        public GarnetStatus DELETE<TContext, TObjectContext>(byte[] key, StoreType storeType, ref TContext context, ref TObjectContext objectContext)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
            where TObjectContext : ITsavoriteContext<byte[], IGarnetObject, ObjectInput, GarnetObjectStoreOutput, long, ObjectSessionFunctions, ObjectStoreFunctions, ObjectStoreAllocator>
        {
            bool found = false;

            if ((storeType == StoreType.Object || storeType == StoreType.All) && !objectStoreBasicContext.IsNull)
            {
                var status = objectContext.Delete(key);
                Debug.Assert(!status.IsPending);
                if (status.Found) found = true;
            }

            if (!found && (storeType == StoreType.Main || storeType == StoreType.All))
            {
                unsafe
                {
                    fixed (byte* ptr = key)
                    {
                        var keySB = SpanByte.FromPinnedPointer(ptr, key.Length);
                        var status = context.Delete(ref keySB);
                        Debug.Assert(!status.IsPending);
                        if (status.Found) found = true;
                    }
                }
            }
            return found ? GarnetStatus.OK : GarnetStatus.NOTFOUND;
        }

        public unsafe GarnetStatus RENAME(ArgSlice oldKeySlice, ArgSlice newKeySlice, StoreType storeType)
        {
            return RENAME(oldKeySlice, newKeySlice, storeType, false, out _);
        }

        /// <summary>
        /// Renames key to newkey if newkey does not yet exist. It returns an error when key does not exist.
        /// </summary>
        /// <param name="oldKeySlice">The old key to be renamed.</param>
        /// <param name="newKeySlice">The new key name.</param>
        /// <param name="storeType">The type of store to perform the operation on.</param>
        /// <returns></returns>
        public unsafe GarnetStatus RENAMENX(ArgSlice oldKeySlice, ArgSlice newKeySlice, StoreType storeType, out int result)
        {
            return RENAME(oldKeySlice, newKeySlice, storeType, true, out result);
        }

        private unsafe GarnetStatus RENAME(ArgSlice oldKeySlice, ArgSlice newKeySlice, StoreType storeType, bool isNX, out int result)
        {
            GarnetStatus returnStatus = GarnetStatus.NOTFOUND;
            result = -1;

            // If same name check return early.
            if (oldKeySlice.ReadOnlySpan.SequenceEqual(newKeySlice.ReadOnlySpan))
            {
                result = 1;
                return GarnetStatus.OK;
            }

            bool createTransaction = false;
            if (txnManager.state != TxnState.Running)
            {
                createTransaction = true;
                txnManager.SaveKeyEntryToLock(oldKeySlice, false, LockType.Exclusive);
                txnManager.SaveKeyEntryToLock(newKeySlice, false, LockType.Exclusive);
                txnManager.Run(true);
            }

            var context = txnManager.LockableContext;
            var objectContext = txnManager.ObjectStoreLockableContext;
            SpanByte oldKey = oldKeySlice.SpanByte;

            if (storeType == StoreType.Main || storeType == StoreType.All)
            {
                try
                {
                    SpanByte input = default;
                    var o = new SpanByteAndMemory();
                    var status = GET(ref oldKey, ref input, ref o, ref context);

                    if (status == GarnetStatus.OK)
                    {
                        Debug.Assert(!o.IsSpanByte);
                        var memoryHandle = o.Memory.Memory.Pin();
                        var ptrVal = (byte*)memoryHandle.Pointer;

                        RespReadUtils.ReadUnsignedLengthHeader(out var headerLength, ref ptrVal, ptrVal + o.Length);

                        // Find expiration time of the old key
                        var expireSpan = new SpanByteAndMemory();
                        var ttlStatus = TTL(ref oldKey, storeType, ref expireSpan, ref context, ref objectContext, true);

                        if (ttlStatus == GarnetStatus.OK && !expireSpan.IsSpanByte)
                        {
                            using var expireMemoryHandle = expireSpan.Memory.Memory.Pin();
                            var expirePtrVal = (byte*)expireMemoryHandle.Pointer;
                            RespReadUtils.TryRead64Int(out var expireTimeMs, ref expirePtrVal, expirePtrVal + expireSpan.Length, out var _);

                            // If the key has an expiration, set the new key with the expiration
                            if (expireTimeMs > 0)
                            {
                                if (isNX)
                                {
                                    // Move payload forward to make space for RespInputHeader and Metadata
                                    var setValue = scratchBufferManager.FormatScratch(RespInputHeader.Size + sizeof(long), new ArgSlice(ptrVal, headerLength));
                                    var setValueSpan = setValue.SpanByte;
                                    var setValuePtr = setValueSpan.ToPointerWithMetadata();
                                    setValueSpan.ExtraMetadata = DateTimeOffset.UtcNow.Ticks + TimeSpan.FromMilliseconds(expireTimeMs).Ticks;
                                    ((RespInputHeader*)(setValuePtr + sizeof(long)))->cmd = RespCommand.SETEXNX;
                                    ((RespInputHeader*)(setValuePtr + sizeof(long)))->flags = 0;
                                    var newKey = newKeySlice.SpanByte;
                                    var setStatus = SET_Conditional(ref newKey, ref setValueSpan, ref context);

                                    // For SET NX `NOTFOUND` means the operation succeeded
                                    result = setStatus == GarnetStatus.NOTFOUND ? 1 : 0;
                                    returnStatus = GarnetStatus.OK;
                                }
                                else
                                {
                                    SETEX(newKeySlice, new ArgSlice(ptrVal, headerLength), TimeSpan.FromMilliseconds(expireTimeMs), ref context);
                                }
                            }
                            else if (expireTimeMs == -1) // Its possible to have expireTimeMs as 0 (Key expired or will be expired now) or -2 (Key does not exist), in those cases we don't SET the new key
                            {
                                if (isNX)
                                {
                                    // Move payload forward to make space for RespInputHeader
                                    var setValue = scratchBufferManager.FormatScratch(RespInputHeader.Size, new ArgSlice(ptrVal, headerLength));
                                    var setValueSpan = setValue.SpanByte;
                                    var setValuePtr = setValueSpan.ToPointerWithMetadata();
                                    ((RespInputHeader*)setValuePtr)->cmd = RespCommand.SETEXNX;
                                    ((RespInputHeader*)setValuePtr)->flags = 0;
                                    var newKey = newKeySlice.SpanByte;
                                    var setStatus = SET_Conditional(ref newKey, ref setValueSpan, ref context);

                                    // For SET NX `NOTFOUND` means the operation succeeded
                                    result = setStatus == GarnetStatus.NOTFOUND ? 1 : 0;
                                    returnStatus = GarnetStatus.OK;
                                }
                                else
                                {
                                    SpanByte newKey = newKeySlice.SpanByte;
                                    var value = SpanByte.FromPinnedPointer(ptrVal, headerLength);
                                    SET(ref newKey, ref value, ref context);
                                }
                            }

                            expireSpan.Memory.Dispose();
                            memoryHandle.Dispose();
                            o.Memory.Dispose();

                            // Delete the old key only when SET NX succeeded
                            if (isNX && result == 1)
                            {
                                DELETE(ref oldKey, StoreType.Main, ref context, ref objectContext);
                            }
                            else if (!isNX)
                            {
                                // Delete the old key
                                DELETE(ref oldKey, StoreType.Main, ref context, ref objectContext);

                                returnStatus = GarnetStatus.OK;
                            }
                        }
                    }
                }
                finally
                {
                    if (createTransaction)
                        txnManager.Commit(true);
                }
            }

            if ((storeType == StoreType.Object || storeType == StoreType.All) && !objectStoreBasicContext.IsNull)
            {
                createTransaction = false;
                if (txnManager.state != TxnState.Running)
                {
                    txnManager.SaveKeyEntryToLock(oldKeySlice, true, LockType.Exclusive);
                    txnManager.SaveKeyEntryToLock(newKeySlice, true, LockType.Exclusive);
                    txnManager.Run(true);
                    createTransaction = true;
                }

                try
                {
                    byte[] oldKeyArray = oldKeySlice.ToArray();
                    var status = GET(oldKeyArray, out var value, ref objectContext);

                    if (status == GarnetStatus.OK)
                    {
                        var valObj = value.garnetObject;
                        byte[] newKeyArray = newKeySlice.ToArray();

                        returnStatus = GarnetStatus.OK;
                        var canSetAndDelete = true;
                        if (isNX)
                        {
                            // Not using EXISTS method to avoid new allocation of Array for key
                            var getNewStatus = GET(newKeyArray, out _, ref objectContext);
                            canSetAndDelete = getNewStatus == GarnetStatus.NOTFOUND;
                        }

                        if (canSetAndDelete)
                        {
                            // valObj already has expiration time, so no need to write expiration logic here
                            SET(newKeyArray, valObj, ref objectContext);

                            // Delete the old key
                            DELETE(oldKeyArray, StoreType.Object, ref context, ref objectContext);

                            result = 1;
                        }
                        else
                        {
                            result = 0;
                        }
                    }
                }
                finally
                {
                    if (createTransaction)
                        txnManager.Commit(true);
                }
            }
            return returnStatus;
        }

        /// <summary>
        /// Returns if key is an existing one in the store.
        /// </summary>
        /// <typeparam name="TContext"></typeparam>
        /// <typeparam name="TObjectContext"></typeparam>
        /// <param name="key">The name of the key to use in the operation</param>
        /// <param name="storeType">The store to operate on.</param>
        /// <param name="context">Basic context for the main store.</param>
        /// <param name="objectContext">Object context for the object store.</param>
        /// <returns></returns>
        public GarnetStatus EXISTS<TContext, TObjectContext>(ArgSlice key, StoreType storeType, ref TContext context, ref TObjectContext objectContext)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
            where TObjectContext : ITsavoriteContext<byte[], IGarnetObject, ObjectInput, GarnetObjectStoreOutput, long, ObjectSessionFunctions, ObjectStoreFunctions, ObjectStoreAllocator>
        {
            GarnetStatus status = GarnetStatus.NOTFOUND;

            if (storeType == StoreType.Main || storeType == StoreType.All)
            {
                var _key = key.SpanByte;
                SpanByte input = default;
                var _output = new SpanByteAndMemory { SpanByte = scratchBufferManager.ViewRemainingArgSlice().SpanByte };
                status = GET(ref _key, ref input, ref _output, ref context);

                if (status == GarnetStatus.OK)
                {
                    if (!_output.IsSpanByte)
                        _output.Memory.Dispose();
                    return status;
                }
            }

            if ((storeType == StoreType.Object || storeType == StoreType.All) && !objectStoreBasicContext.IsNull)
            {
                status = GET(key.ToArray(), out _, ref objectContext);
            }

            return status;
        }

        /// <summary>
        /// Set a timeout on key
        /// </summary>
        /// <typeparam name="TContext"></typeparam>
        /// <typeparam name="TObjectContext"></typeparam>
        /// <param name="key">The key to set the timeout on.</param>
        /// <param name="expiryMs">Milliseconds value for the timeout.</param>
        /// <param name="timeoutSet">True when the timeout was properly set.</param>
        /// <param name="storeType">The store to operate on.</param>
        /// <param name="expireOption">>Flags to use for the operation.</param>
        /// <param name="context">Basic context for the main store.</param>
        /// <param name="objectStoreContext">Object context for the object store.</param>
        /// <returns></returns>
        public unsafe GarnetStatus EXPIRE<TContext, TObjectContext>(ArgSlice key, ArgSlice expiryMs, out bool timeoutSet, StoreType storeType, ExpireOption expireOption, ref TContext context, ref TObjectContext objectStoreContext)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
            where TObjectContext : ITsavoriteContext<byte[], IGarnetObject, ObjectInput, GarnetObjectStoreOutput, long, ObjectSessionFunctions, ObjectStoreFunctions, ObjectStoreAllocator>
            => EXPIRE(key, TimeSpan.FromMilliseconds(NumUtils.BytesToLong(expiryMs.Length, expiryMs.ptr)), out timeoutSet, storeType, expireOption, ref context, ref objectStoreContext);

        /// <summary>
        /// Set a timeout on key.
        /// </summary>
        /// <typeparam name="TContext"></typeparam>
        /// <typeparam name="TObjectContext"></typeparam>
        /// <param name="key">The key to set the timeout on.</param>
        /// <param name="expiry">The timespan value to set the expiration for.</param>
        /// <param name="timeoutSet">True when the timeout was properly set.</param>
        /// <param name="storeType">The store to operate on.</param>
        /// <param name="expireOption">Flags to use for the operation.</param>
        /// <param name="context">Basic context for the main store</param>
        /// <param name="objectStoreContext">Object context for the object store</param>
        /// <param name="milliseconds">When true the command executed is PEXPIRE, expire by default.</param>
        /// <returns>Return GarnetStatus.OK when key found, else GarnetStatus.NOTFOUND</returns>
        public unsafe GarnetStatus EXPIRE<TContext, TObjectContext>(ArgSlice key, TimeSpan expiry, out bool timeoutSet, StoreType storeType, ExpireOption expireOption, ref TContext context, ref TObjectContext objectStoreContext, bool milliseconds = false)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
            where TObjectContext : ITsavoriteContext<byte[], IGarnetObject, ObjectInput, GarnetObjectStoreOutput, long, ObjectSessionFunctions, ObjectStoreFunctions, ObjectStoreAllocator>
        {
            return EXPIRE(key, DateTimeOffset.UtcNow.Ticks + expiry.Ticks, out timeoutSet, storeType, expireOption, ref context, ref objectStoreContext, milliseconds ? RespCommand.PEXPIRE : RespCommand.EXPIRE);
        }

        /// <summary>
        /// Set a timeout on key using absolute Unix timestamp (seconds since January 1, 1970).
        /// </summary>
        /// <typeparam name="TContext"></typeparam>
        /// <typeparam name="TObjectContext"></typeparam>
        /// <param name="key">The key to set the timeout on.</param>
        /// <param name="expiryTimestamp">Absolute Unix timestamp</param>
        /// <param name="timeoutSet">True when the timeout was properly set.</param>
        /// <param name="storeType">The store to operate on.</param>
        /// <param name="expireOption">Flags to use for the operation.</param>
        /// <param name="context">Basic context for the main store</param>
        /// <param name="objectStoreContext">Object context for the object store</param>
        /// <param name="milliseconds">When true, <paramref name="expiryTimestamp"/> is treated as milliseconds else seconds</param>
        /// <returns>Return GarnetStatus.OK when key found, else GarnetStatus.NOTFOUND</returns>
        public unsafe GarnetStatus EXPIREAT<TContext, TObjectContext>(ArgSlice key, long expiryTimestamp, out bool timeoutSet, StoreType storeType, ExpireOption expireOption, ref TContext context, ref TObjectContext objectStoreContext, bool milliseconds = false)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
            where TObjectContext : ITsavoriteContext<byte[], IGarnetObject, ObjectInput, GarnetObjectStoreOutput, long, ObjectSessionFunctions, ObjectStoreFunctions, ObjectStoreAllocator>
        {
            var expiryTimestampTicks = milliseconds ? ConvertUtils.UnixTimestampInMillisecondsToTicks(expiryTimestamp) : ConvertUtils.UnixTimestampInSecondsToTicks(expiryTimestamp);
            return EXPIRE(key, expiryTimestampTicks, out timeoutSet, storeType, expireOption, ref context, ref objectStoreContext, milliseconds ? RespCommand.PEXPIRE : RespCommand.EXPIRE);
        }

        /// <summary>
        /// Set a timeout on key using ticks.
        /// </summary>
        /// <typeparam name="TContext"></typeparam>
        /// <typeparam name="TObjectContext"></typeparam>
        /// <param name="key">The key to set the timeout on.</param>
        /// <param name="expiryInTicks">The timestamp in ticks</param>
        /// <param name="timeoutSet">True when the timeout was properly set.</param>
        /// <param name="storeType">The store to operate on.</param>
        /// <param name="expireOption">Flags to use for the operation.</param>
        /// <param name="context">Basic context for the main store</param>
        /// <param name="objectStoreContext">Object context for the object store</param>
        /// <param name="respCommand">Resp Command to be executed.</param>
        /// <returns>Return GarnetStatus.OK when key found, else GarnetStatus.NOTFOUND</returns>
        private unsafe GarnetStatus EXPIRE<TContext, TObjectContext>(ArgSlice key, long expiryInTicks, out bool timeoutSet, StoreType storeType, ExpireOption expireOption, ref TContext context, ref TObjectContext objectStoreContext, RespCommand respCommand)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
            where TObjectContext : ITsavoriteContext<byte[], IGarnetObject, ObjectInput, GarnetObjectStoreOutput, long, ObjectSessionFunctions, ObjectStoreFunctions, ObjectStoreAllocator>
        {
            byte* pbCmdInput = stackalloc byte[sizeof(int) + sizeof(long) + RespInputHeader.Size + sizeof(byte)];
            *(int*)pbCmdInput = sizeof(long) + RespInputHeader.Size;
            ((RespInputHeader*)(pbCmdInput + sizeof(int) + sizeof(long)))->cmd = respCommand;
            ((RespInputHeader*)(pbCmdInput + sizeof(int) + sizeof(long)))->flags = 0;

            *(pbCmdInput + sizeof(int) + sizeof(long) + RespInputHeader.Size) = (byte)expireOption;
            ref var input = ref SpanByte.Reinterpret(pbCmdInput);

            input.ExtraMetadata = expiryInTicks;

            var rmwOutput = stackalloc byte[ObjectOutputHeader.Size];
            var output = new SpanByteAndMemory(SpanByte.FromPinnedPointer(rmwOutput, ObjectOutputHeader.Size));
            timeoutSet = false;

            bool found = false;

            if (storeType == StoreType.Main || storeType == StoreType.All)
            {
                var _key = key.SpanByte;
                var status = context.RMW(ref _key, ref input, ref output);

                if (status.IsPending)
                    CompletePendingForSession(ref status, ref output, ref context);
                if (status.Found) found = true;
            }

            if (!found && (storeType == StoreType.Object || storeType == StoreType.All) &&
                !objectStoreBasicContext.IsNull)
            {
                var parseState = new SessionParseState();
                ArgSlice[] parseStateBuffer = default;

                var expireOptionLength = NumUtils.NumDigits((byte)expireOption);
                var expireOptionPtr = stackalloc byte[expireOptionLength];
                NumUtils.IntToBytes((byte)expireOption, expireOptionLength, ref expireOptionPtr);
                expireOptionPtr -= expireOptionLength;
                var expireOptionSlice = new ArgSlice(expireOptionPtr, expireOptionLength);

                var extraMetadataLength = NumUtils.NumDigitsInLong(input.ExtraMetadata);
                var extraMetadataPtr = stackalloc byte[extraMetadataLength];
                NumUtils.LongToBytes(input.ExtraMetadata, extraMetadataLength, ref extraMetadataPtr);
                extraMetadataPtr -= extraMetadataLength;
                var extraMetadataSlice = new ArgSlice(extraMetadataPtr, extraMetadataLength);

                parseState.InitializeWithArguments(ref parseStateBuffer, expireOptionSlice, extraMetadataSlice);

                var objInput = new ObjectInput
                {
                    header = new RespInputHeader
                    {
                        cmd = respCommand,
                        type = GarnetObjectType.Expire,
                    },
                    parseState = parseState,
                    parseStateStartIdx = 0,
                };

                // Retry on object store
                var objOutput = new GarnetObjectStoreOutput { spanByteAndMemory = output };
                var keyBytes = key.ToArray();
                var status = objectStoreContext.RMW(ref keyBytes, ref objInput, ref objOutput);

                if (status.IsPending)
                    CompletePendingForObjectStoreSession(ref status, ref objOutput, ref objectStoreContext);
                if (status.Found) found = true;

                output = objOutput.spanByteAndMemory;
            }

            Debug.Assert(output.IsSpanByte);
            if (found) timeoutSet = ((ObjectOutputHeader*)output.SpanByte.ToPointer())->result1 == 1;

            return found ? GarnetStatus.OK : GarnetStatus.NOTFOUND;
        }

        public unsafe GarnetStatus PERSIST<TContext, TObjectContext>(ArgSlice key, StoreType storeType, ref TContext context, ref TObjectContext objectStoreContext)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
            where TObjectContext : ITsavoriteContext<byte[], IGarnetObject, ObjectInput, GarnetObjectStoreOutput, long, ObjectSessionFunctions, ObjectStoreFunctions, ObjectStoreAllocator>
        {
            GarnetStatus status = GarnetStatus.NOTFOUND;

            int inputSize = sizeof(int) + RespInputHeader.Size;
            byte* pbCmdInput = stackalloc byte[inputSize];
            byte* pcurr = pbCmdInput;
            *(int*)pcurr = inputSize - sizeof(int);
            pcurr += sizeof(int);
            (*(RespInputHeader*)pcurr).cmd = RespCommand.PERSIST;
            (*(RespInputHeader*)pcurr).flags = 0;

            byte* pbOutput = stackalloc byte[8];
            var o = new SpanByteAndMemory(pbOutput, 8);

            if (storeType == StoreType.Main || storeType == StoreType.All)
            {
                var _key = key.SpanByte;
                var _status = context.RMW(ref _key, ref Unsafe.AsRef<SpanByte>(pbCmdInput), ref o);

                if (_status.IsPending)
                    CompletePendingForSession(ref _status, ref o, ref context);

                Debug.Assert(o.IsSpanByte);
                if (o.SpanByte.AsReadOnlySpan()[0] == 1)
                    status = GarnetStatus.OK;
            }

            if (status == GarnetStatus.NOTFOUND && (storeType == StoreType.Object || storeType == StoreType.All) && !objectStoreBasicContext.IsNull)
            {
                // Retry on object store
                var objInput = new ObjectInput
                {
                    header = new RespInputHeader
                    {
                        cmd = RespCommand.PERSIST,
                        type = GarnetObjectType.Persist,
                    },
                };

                var objO = new GarnetObjectStoreOutput { spanByteAndMemory = o };
                var _key = key.ToArray();
                var _status = objectStoreContext.RMW(ref _key, ref objInput, ref objO);

                if (_status.IsPending)
                    CompletePendingForObjectStoreSession(ref _status, ref objO, ref objectStoreContext);

                Debug.Assert(o.IsSpanByte);
                if (o.SpanByte.AsReadOnlySpan().Slice(0, CmdStrings.RESP_RETURN_VAL_1.Length)
                    .SequenceEqual(CmdStrings.RESP_RETURN_VAL_1))
                    status = GarnetStatus.OK;
            }

            return status;
        }

        /// <summary>
        /// For existing keys - overwrites part of the value at a specified offset (in-place if possible)
        /// For non-existing keys - creates a new string with the value at a specified offset (padded with '\0's)
        /// </summary>
        /// <typeparam name="TContext"></typeparam>
        /// <param name="key">The key for which to set the range</param>
        /// <param name="value">The value to place at an offset</param>
        /// <param name="offset">The offset at which to place the value</param>
        /// <param name="output">The length of the updated string</param>
        /// <param name="context">Basic context for the main store</param>
        /// <returns></returns>
        public unsafe GarnetStatus SETRANGE<TContext>(ArgSlice key, ArgSlice value, int offset, ref ArgSlice output, ref TContext context)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
        {
            var sbKey = key.SpanByte;
            SpanByteAndMemory sbmOut = new(output.SpanByte);

            // Total size + Offset size + New value size + Address of new value
            int inputSize = sizeof(int) + sizeof(int) + sizeof(int) + sizeof(long);
            byte* pbCmdInput = stackalloc byte[inputSize];

            byte* pcurr = pbCmdInput;
            *(int*)pcurr = inputSize - sizeof(int);
            pcurr += sizeof(int);
            (*(RespInputHeader*)(pcurr)).cmd = RespCommand.SETRANGE;
            (*(RespInputHeader*)(pcurr)).flags = 0;
            pcurr += RespInputHeader.Size;
            *(int*)(pcurr) = offset;
            pcurr += sizeof(int);
            *(int*)pcurr = value.Length;
            pcurr += sizeof(int);
            *(long*)pcurr = (long)(value.ptr);

            var status = context.RMW(ref sbKey, ref Unsafe.AsRef<SpanByte>(pbCmdInput), ref sbmOut);
            if (status.IsPending)
                CompletePendingForSession(ref status, ref sbmOut, ref context);

            Debug.Assert(sbmOut.IsSpanByte);
            output.length = sbmOut.Length;

            return GarnetStatus.OK;
        }

        public GarnetStatus Increment<TContext>(ArgSlice key, ArgSlice input, ref ArgSlice output, ref TContext context)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
        {
            var _key = key.SpanByte;
            var _input = input.SpanByte;
            SpanByteAndMemory _output = new(output.SpanByte);

            var status = context.RMW(ref _key, ref _input, ref _output);
            if (status.IsPending)
                CompletePendingForSession(ref status, ref _output, ref context);
            Debug.Assert(_output.IsSpanByte);
            output.length = _output.Length;
            return GarnetStatus.OK;
        }

        public unsafe GarnetStatus Increment<TContext>(ArgSlice key, out long output, long increment, ref TContext context)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
        {
            var cmd = RespCommand.INCRBY;
            if (increment < 0)
            {
                cmd = RespCommand.DECRBY;
                increment = -increment;
            }

            var incrementNumDigits = NumUtils.NumDigitsInLong(increment);
            var inputByteSize = RespInputHeader.Size + incrementNumDigits;
            var input = stackalloc byte[inputByteSize];
            ((RespInputHeader*)input)->cmd = cmd;
            ((RespInputHeader*)input)->flags = 0;
            var longOutput = input + RespInputHeader.Size;
            NumUtils.LongToBytes(increment, incrementNumDigits, ref longOutput);

            const int outputBufferLength = NumUtils.MaximumFormatInt64Length + 1;
            byte* outputBuffer = stackalloc byte[outputBufferLength];

            var _key = key.SpanByte;
            var _input = SpanByte.FromPinnedPointer(input, inputByteSize);
            var _output = new SpanByteAndMemory(outputBuffer, outputBufferLength);

            var status = context.RMW(ref _key, ref _input, ref _output);
            if (status.IsPending)
                CompletePendingForSession(ref status, ref _output, ref context);
            Debug.Assert(_output.IsSpanByte);
            Debug.Assert(_output.Length == outputBufferLength);

            output = NumUtils.BytesToLong(_output.Length, outputBuffer);
            return GarnetStatus.OK;
        }

        public void WATCH(ArgSlice key, StoreType type)
        {
            txnManager.Watch(key, type);
            txnManager.VerifyKeyOwnership(key, LockType.Shared);
        }

        public unsafe void WATCH(byte[] key, StoreType type)
        {
            fixed (byte* ptr = key)
            {
                WATCH(new ArgSlice(ptr, key.Length), type);
            }
        }

        public unsafe GarnetStatus SCAN<TContext>(long cursor, ArgSlice match, long count, ref TContext context)
        {
            return GarnetStatus.OK;
        }

        public GarnetStatus GetKeyType<TContext, TObjectContext>(ArgSlice key, out string keyType, ref TContext context, ref TObjectContext objectContext)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
            where TObjectContext : ITsavoriteContext<byte[], IGarnetObject, ObjectInput, GarnetObjectStoreOutput, long, ObjectSessionFunctions, ObjectStoreFunctions, ObjectStoreAllocator>
        {
            keyType = "string";
            // Check if key exists in Main store
            var status = EXISTS(key, StoreType.Main, ref context, ref objectContext);

            // If key was not found in the main store then it is an object
            if (status != GarnetStatus.OK && !objectStoreBasicContext.IsNull)
            {
                status = GET(key.ToArray(), out GarnetObjectStoreOutput output, ref objectContext);
                if (status == GarnetStatus.OK)
                {
                    if ((output.garnetObject as SortedSetObject) != null)
                    {
                        keyType = "zset";
                    }
                    else if ((output.garnetObject as ListObject) != null)
                    {
                        keyType = "list";
                    }
                    else if ((output.garnetObject as SetObject) != null)
                    {
                        keyType = "set";
                    }
                    else if ((output.garnetObject as HashObject) != null)
                    {
                        keyType = "hash";
                    }
                }
                else
                {
                    keyType = "none";
                    status = GarnetStatus.NOTFOUND;
                }
            }
            return status;
        }

        public GarnetStatus MemoryUsageForKey<TContext, TObjectContext>(ArgSlice key, out long memoryUsage, ref TContext context, ref TObjectContext objectContext, int samples = 0)
            where TContext : ITsavoriteContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator>
            where TObjectContext : ITsavoriteContext<byte[], IGarnetObject, ObjectInput, GarnetObjectStoreOutput, long, ObjectSessionFunctions, ObjectStoreFunctions, ObjectStoreAllocator>
        {
            memoryUsage = -1;

            // Check if key exists in Main store
            var status = GET(key, out ArgSlice keyValue, ref context);

            if (status == GarnetStatus.NOTFOUND)
            {
                status = GET(key.ToArray(), out GarnetObjectStoreOutput objectValue, ref objectContext);
                if (status != GarnetStatus.NOTFOUND)
                {
                    memoryUsage = RecordInfo.GetLength() + (2 * IntPtr.Size) + // Log record length
                        Utility.RoundUp(key.SpanByte.Length, IntPtr.Size) + MemoryUtils.ByteArrayOverhead + // Key allocation in heap with overhead
                        objectValue.garnetObject.Size; // Value allocation in heap
                }
            }
            else
            {
                memoryUsage = RecordInfo.GetLength() + Utility.RoundUp(key.SpanByte.TotalSize, RecordInfo.GetLength()) + Utility.RoundUp(keyValue.SpanByte.TotalSize, RecordInfo.GetLength());
            }

            return status;
        }
    }
}