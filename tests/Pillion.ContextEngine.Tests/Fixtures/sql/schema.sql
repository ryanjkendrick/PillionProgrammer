-- Orders schema.
CREATE TABLE dbo.Orders (
    Id INT NOT NULL PRIMARY KEY,
    Customer NVARCHAR(100) NOT NULL -- who; ordered
);
GO

CREATE PROCEDURE dbo.GetOrders
    @Customer NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, Customer
    FROM dbo.Orders
    WHERE Customer = @Customer;
END
GO

INSERT INTO dbo.Orders (Id, Customer) VALUES (1, 'Ann; Smith');
INSERT INTO dbo.Orders (Id, Customer) VALUES (2, 'Bob');
INSERT INTO dbo.Orders (Id, Customer) VALUES (3, 'Cy');

SELECT COUNT(*) FROM dbo.Orders;
