﻿using SecureSocketProtocol3.Network;
using SecureSocketProtocol3.Network.Headers;
using SecureSocketProtocol3.Network.MazingHandshake;
using SecureSocketProtocol3.Network.Messages.TCP;
using SecureSocketProtocol3.Utils;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SecureSocketProtocol3.Network.Messages;

/*
    The MIT License (MIT)

    Copyright (c) 2016 AnguisCaptor

    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in all
    copies or substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    SOFTWARE.
*/

namespace SecureSocketProtocol3.Security.Handshakes
{
    public class MazeHandshake : Handshake
    {
        internal Mazing ClientHS { get; private set; }
        internal ServerMaze ServerHS { get; set; }

        public delegate User.UserDbInfo FindUserCallback(string EncryptedPublicKeyHash);
        public event FindUserCallback onFindUser;

        private Mazing _handshake
        {
            get
            {
                return Client.IsServerSided ? ServerHS : ClientHS;
            }
        }

        public Size MazeSize { get; set; }
        public ushort StepSize { get; set; }
        public ushort MazeCount { get; set; }

        public string Username { get; set; }
        public string Password { get; set; }

        public Stream[] PrivateKeyFiles { get; set; }
        public Stream PublicKeyFile { get; set; }

        public MazeHandshake(SSPClient Client)
            : base(Client)
        {

        }

        public MazeHandshake(SSPClient Client, Size MazeSize, ushort StepSize, ushort MazeCount)
            : this(Client)
        {
            this.MazeSize = MazeSize;
            this.StepSize = StepSize;
            this.MazeCount = MazeCount;
        }

        public MazeHandshake(SSPClient Client, Size MazeSize, ushort StepSize, ushort MazeCount, string Username, string Password,
                             Stream[] PrivateKeyFiles, Stream PublicKeyFile)
            : this(Client, MazeSize, StepSize, MazeCount)
        {
            this.Username = Username;
            this.Password = Password;
            this.PrivateKeyFiles = PrivateKeyFiles;
            this.PublicKeyFile = PublicKeyFile;

            if (String.IsNullOrEmpty(Username))
                throw new ArgumentException("Username");
            if (String.IsNullOrEmpty(Password))
                throw new ArgumentException("Password");
            if (PublicKeyFile == null)
                throw new ArgumentException("PublicKeyFile");
            if (PublicKeyFile.Length < 128)
                throw new ArgumentException("PublicKeyFile must be >=128 in length");
            if (PrivateKeyFiles == null)
                throw new ArgumentException("PrivateKeyFiles");
            if (PrivateKeyFiles.Length == 0)
                throw new ArgumentException("There must be atleast 1 private key file");
        }



        public override void onReceiveMessage(Network.Messages.IMessage Message)
        {
            if ((Message as MsgHandshake) == null)
            {
                //what happend ?
                Client.Disconnect();
                return;
            }

            byte[] responseData = new byte[0];
            MazeErrorCode errorCode = MazeErrorCode.Error;
            byte[] HandshakeData = (Message as MsgHandshake).Data;

            if (_handshake == null)
            {
                //error could occur on a unexpected disconnect
                Client.Disconnect();
                return;
            }

            errorCode = _handshake.onReceiveData(HandshakeData, ref responseData);

            if (errorCode != MazeErrorCode.Finished && errorCode != MazeErrorCode.Success && Client.TimingConfiguration.Enable_Timing)
            {
                //something went wrong, annoy the attacker
                Thread.Sleep(Client.TimingConfiguration.Authentication_WrongPassword);
                Client.Disconnect();
                return;
            }

            if (responseData.Length > 0)
            {
                Client.Connection.SendMessage(new MsgHandshake(responseData), new SystemHeader());
            }

            if (Client == null || Client.Connection == null)
            {
                //error could occur on a unexpected disconnect
                return;
            }

            HandshakeSync.Value = errorCode;
            if (errorCode != MazeErrorCode.Finished && errorCode != MazeErrorCode.Success)
            {
                Client.Disconnect();
            }
            else if (errorCode == MazeErrorCode.Finished)
            {
                //let's tell it's completed and apply the new key
                Client.Connection.ApplyNewKey(_handshake.FinalKey, _handshake.FinalSalt);

                if (Client.IsServerSided)
                {
                    if (_handshake as ServerMaze != null)
                    {
                        Client.Username = (_handshake as ServerMaze).Username;
                    }

                    /*try
                    {
                        Client.onBeforeConnect();
                    }
                    catch (Exception ex)
                    {
                        SysLogger.Log(ex.Message, SysLogType.Error);
                        Client.onException(ex, ErrorType.UserLand);
                        return; //don't send that we're ready since we're clearly not at this point
                    }

                    try
                    {
                        Client.onConnect();
                    }
                    catch (Exception ex)
                    {
                        SysLogger.Log(ex.Message, SysLogType.Error, ex);
                        Client.onException(ex, ErrorType.UserLand);
                        return; //don't send that we're ready since we're clearly not at this point
                    }*/
                }
                else
                {
                    Finish();
                }
            }
        }

        public override void onStartHandshake()
        {
            if (Client.IsServerSided)
            {
                ServerHS = new ServerMaze(MazeSize, MazeCount, StepSize);
                ServerHS.onFindKeyInDatabase += ServerHS_onFindKeyInDatabase;
            }
            else if (!Client.IsServerSided) //client side
            {
                User user = new User(Username, Password, new List<Stream>(PrivateKeyFiles), PublicKeyFile);
                user.GenKey(Client, SessionSide.Client, MazeSize, MazeCount, StepSize);
                this.ClientHS = user.MazeHandshake;
                byte[] encryptedPublicKey = ClientHS.GetEncryptedPublicKey();

                byte[] byteCode = ClientHS.GetByteCode();
                Client.Connection.SendMessage(new MsgHandshake(byteCode), new SystemHeader());

                //send our encrypted public key
                Client.Connection.SendMessage(new MsgHandshake(encryptedPublicKey), new SystemHeader());
            }
        }

        public override void onRegisterMessages(MessageHandler messageHandler)
        {

        }

        public override void onFinish()
        {
            if (!Client.IsServerSided)
            {
                //re-calculate the private keys
                for (int i = 0; i < PrivateKeyFiles.Length; i++)
                {
                    _handshake.RecalculatePrivateKey(PrivateKeyFiles[i]);
                }
            }
        }

        private bool ServerHS_onFindKeyInDatabase(string EncryptedHash, ref byte[] Key, ref byte[] Salt, ref byte[] PublicKey, ref string Username)
        {
            lock (ServerHS)
            {
                try
                {
                    User.UserDbInfo user = onFindUser(EncryptedHash);

                    if (user == null)
                        return false;

                    Key = user.Key.getBytes();
                    Salt = user.PrivateSalt.getBytes();
                    PublicKey = user.PublicKey;
                    Username = user.UsernameStr;
                    return true;
                }
                catch (Exception ex)
                {
                    SysLogger.Log(ex.Message, SysLogType.Error, ex);
                    return false;
                }
            }
        }

        /// <summary>
        /// Create a new instance of User
        /// </summary>
        /// <param name="Username">The Username for the user</param>
        /// <param name="Password">The Password for the user</param>
        /// <param name="PrivateKeys">The Private Key(s) that are being used to Encrypt the Session</param>
        /// <param name="PublicKey">The Public Key to indentify the user</param>
        public static User RegisterUser(Size MazeSize, ushort MazeCount, ushort StepSize, string Username, string Password,
                                        List<Stream> PrivateKeys, Stream PublicKey)
        {
            User user = new User(Username, Password, PrivateKeys, PublicKey);
            user.GenKey(SessionSide.Server, MazeSize, MazeCount, StepSize);
            return user;
        }
    }
}