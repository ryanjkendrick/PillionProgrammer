Option Strict On
Imports System
Imports System.Collections.Generic

Namespace Fixtures.Sample
    ''' <summary>Greets people.</summary>
    <Serializable>
    Public Class Greeter
        Inherits Object
        Implements IDisposable

        Private ReadOnly _prefix As String
        Private _count As Integer

        ''' <summary>Creates a greeter.</summary>
        Public Sub New(prefix As String)
            _prefix = prefix
        End Sub

        Public ReadOnly Property Count As Integer
            Get
                Return _count
            End Get
        End Property

        ''' <summary>Greets someone.</summary>
        Public Function Greet(name As String) As String
            _count += 1
            Return _prefix & ", " & name
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
        End Sub
    End Class

    Public Enum Mood
        Happy
        Sad
    End Enum
End Namespace
