-- Kiểm tra và xóa database nếu đã tồn tại
IF DB_ID('TCP_Server_Client') IS NOT NULL
BEGIN
    ALTER DATABASE TCP_Server_Client SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE TCP_Server_Client;
END
GO

-- Tạo mới database
CREATE DATABASE TCP_Server_Client;
GO

USE TCP_Server_Client;
GO

-- Bảng User
CREATE TABLE [User] (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    FullName NVARCHAR(100) NOT NULL,
    Address NVARCHAR(200),
    Gender INT -- 0: Nữ, 1: Nam, 2: Khác
);
GO

-- Bảng Product
CREATE TABLE Product (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL
);
GO

-- Bảng Order
CREATE TABLE [Order] (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    UserId INT NOT NULL,
    OrderDate DATETIME NOT NULL,
    Status INT, -- 0: Pending, 1: Done, 2: Cancelled
    FOREIGN KEY (UserId) REFERENCES [User](Id)
);
GO

-- Bảng OrderDetail
CREATE TABLE OrderDetail (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    OrderId INT NOT NULL,
    ProductId INT NOT NULL,
    Quantity INT NOT NULL,
    FOREIGN KEY (OrderId) REFERENCES [Order](Id),
    FOREIGN KEY (ProductId) REFERENCES Product(Id)
);
GO

-- Thêm dữ liệu mẫu User
INSERT INTO [User] (FullName, Address, Gender)
VALUES 
('John Smith', 'New York', 1),
('Emily Johnson', 'Los Angeles', 0),
('Michael Brown', 'Chicago', 1),
('Sophia Davis', 'Houston', 0),
('Daniel Wilson', 'San Francisco', 1);
GO

-- Thêm dữ liệu mẫu Product
INSERT INTO Product (Name)
VALUES 
('Logitech M90 Mouse'),
('DareU EK87 Keyboard'),
('Dell 24 inch Monitor'),
('Razer Kraken Headset'),
('HP Pavilion 15 Laptop');
GO

-- Thêm dữ liệu mẫu Order
INSERT INTO [Order] (UserId, OrderDate, Status)
VALUES 
(1, '2024-10-10', 1),
(1, '2024-11-05', 0),
(2, '2024-09-15', 1),
(3, '2024-10-01', 2);
GO

-- Thêm dữ liệu mẫu OrderDetail
INSERT INTO OrderDetail (OrderId, ProductId, Quantity)
VALUES
-- Order 1 by John Smith
(1, 1, 1),  -- Logitech M90 Mouse
(1, 2, 2),  -- DareU EK87 Keyboard

-- Order 2 by John Smith
(2, 3, 1),  -- Dell 24 inch Monitor

-- Order by Emily Johnson
(3, 5, 1),  -- HP Pavilion 15 Laptop

-- Order by Michael Brown
(4, 4, 1);  -- Razer Kraken Headset
GO
