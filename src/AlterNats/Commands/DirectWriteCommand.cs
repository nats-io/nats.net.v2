﻿using AlterNats.Internal;
using System.Text;

namespace AlterNats.Commands;

// public fore optimize reusing
public sealed class DirectWriteCommand : ICommand
{
    readonly byte[] protocol;

    /// <param name="protocol">raw command without \r\n</param>
    /// <param name="repeatCount">repeating count.</param>
    public DirectWriteCommand(string protocol, int repeatCount)
    {
        if (repeatCount < 1) throw new ArgumentException("repeatCount should >= 1, repeatCount:" + repeatCount);

        if (repeatCount == 1)
        {
            this.protocol = Encoding.UTF8.GetBytes(protocol + "\r\n");
        }
        else
        {
            var bin = Encoding.UTF8.GetBytes(protocol + "\r\n");
            this.protocol = new byte[bin.Length * repeatCount];
            var span = this.protocol.AsSpan();
            for (int i = 0; i < repeatCount; i++)
            {
                bin.CopyTo(span);
                span = span.Slice(bin.Length);
            }
        }
    }

    void ICommand.Return(ObjectPool pool)
    {
    }

    void ICommand.Write(ProtocolWriter writer)
    {
        writer.WriteRaw(protocol);
    }
}
