﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using Microsoft.Extensions.Logging;

namespace Garnet.server
{
    /// <summary>
    /// Command registration API
    /// </summary>
    public class RegisterApi
    {
        readonly GarnetProvider provider;

        /// <summary>
        /// Construct new Register API instance
        /// </summary>
        public RegisterApi(GarnetProvider provider)
        {
            this.provider = provider;
        }

        /// <summary>
        /// Register custom command with Garnet
        /// </summary>
        /// <param name="name">Name of command</param>
        /// <param name="type">Type of command (e.g., read)</param>
        /// <param name="customFunctions">Custom functions for command logic</param>
        /// <param name="commandInfo">RESP command info</param>
        /// <param name="commandDocs">RESP command docs</param>
        /// <param name="expirationTicks">
        /// Expiration for value, in ticks.
        /// -1 => remove existing expiration metadata;
        ///  0 => retain whatever it is currently (or no expiration if this is a new entry) - this is the default;
        /// >0 => set expiration to given value.
        /// </param>
        /// <returns>ID of the registered command</returns>
        public int NewCommand(string name, CommandType type, CustomRawStringFunctions customFunctions, RespCommandsInfo commandInfo = null, RespCommandDocs commandDocs = null, long expirationTicks = 0)
            => provider.StoreWrapper.customCommandManager.Register(name, type, customFunctions, commandInfo, commandDocs, expirationTicks);

        /// <summary>
        /// Register transaction procedure with Garnet
        /// </summary>
        /// <param name="name">Name of command</param>
        /// <param name="proc">Custom stored procedure</param>
        /// <param name="commandInfo">RESP command info</param>
        /// <param name="commandDocs">RESP command docs</param>
        /// <returns>ID of the registered command</returns>
        public int NewTransactionProc(string name, Func<CustomTransactionProcedure> proc, RespCommandsInfo commandInfo = null, RespCommandDocs commandDocs = null)
            => provider.StoreWrapper.customCommandManager.Register(name, proc, commandInfo, commandDocs);

        /// <summary>
        /// Register object type with server
        /// </summary>
        /// <param name="factory">Factory for object type</param>
        /// <returns></returns>
        public int NewType(CustomObjectFactory factory)
            => provider.StoreWrapper.customCommandManager.RegisterType(factory);

        /// <summary>
        /// Register custom command with Garnet
        /// </summary>
        /// <param name="name">Name of command</param>
        /// <param name="commandType">Type of command (e.g., read)</param>
        /// <param name="factory">Custom factory for object</param>
        /// <param name="customObjectFunctions">Custom object command implementation</param>
        /// <param name="commandInfo">RESP command info</param>
        /// <param name="commandDocs">RESP command docs</param>
        /// <returns>ID of the registered command</returns>
        public (int objectTypeId, int subCommandId) NewCommand(string name, CommandType commandType, CustomObjectFactory factory, CustomObjectFunctions customObjectFunctions, RespCommandsInfo commandInfo = null, RespCommandDocs commandDocs = null)
            => provider.StoreWrapper.customCommandManager.Register(name, commandType, factory, commandInfo, commandDocs, customObjectFunctions);

        /// <summary>
        /// Register custom procedure with Garnet
        /// </summary>
        /// <param name="name"></param>
        /// <param name="customProcedure"></param>
        /// <param name="commandInfo"></param>
        /// <param name="commandDocs"></param>
        /// <returns></returns>
        public int NewProcedure(string name, Func<CustomProcedure> customProcedure, RespCommandsInfo commandInfo = null, RespCommandDocs commandDocs = null)
            => provider.StoreWrapper.customCommandManager.Register(name, customProcedure, commandInfo, commandDocs);

        /// <summary>
        /// Register custom module with Garnet
        /// </summary>
        /// <param name="module"></param>
        /// <param name="moduleArgs"></param>
        /// <param name="logger"></param>
        /// <param name="errorMessage"></param>
        /// <returns></returns>
        public bool NewModule(ModuleBase module, string[] moduleArgs, out ReadOnlySpan<byte> errorMessage, ILogger logger = null)
            => provider.StoreWrapper.customCommandManager.RegisterModule(module, moduleArgs, logger, out errorMessage);
    }
}