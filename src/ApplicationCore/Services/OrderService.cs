using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Ardalis.GuardClauses;
using Azure.Messaging.ServiceBus;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;
using Newtonsoft.Json;

namespace Microsoft.eShopWeb.ApplicationCore.Services;

public class OrderService : IOrderService
{
    private readonly IRepository<Order> _orderRepository;
    private readonly IUriComposer _uriComposer;
    private readonly IRepository<Basket> _basketRepository;
    private readonly IRepository<CatalogItem> _itemRepository;

    public OrderService(IRepository<Basket> basketRepository,
        IRepository<CatalogItem> itemRepository,
        IRepository<Order> orderRepository,
        IUriComposer uriComposer)
    {
        _orderRepository = orderRepository;
        _uriComposer = uriComposer;
        _basketRepository = basketRepository;
        _itemRepository = itemRepository;
    }

    public async Task CreateOrderAsync(int basketId, Address shippingAddress)
    {
        var basketSpec = new BasketWithItemsSpecification(basketId);
        var basket = await _basketRepository.GetBySpecAsync(basketSpec);

        Guard.Against.NullBasket(basketId, basket);
        Guard.Against.EmptyBasketOnCheckout(basket.Items);

        var catalogItemsSpecification = new CatalogItemsSpecification(basket.Items.Select(item => item.CatalogItemId).ToArray());
        var catalogItems = await _itemRepository.ListAsync(catalogItemsSpecification);

        var items = basket.Items.Select(basketItem =>
        {
            var catalogItem = catalogItems.First(c => c.Id == basketItem.CatalogItemId);
            var itemOrdered = new CatalogItemOrdered(catalogItem.Id, catalogItem.Name, _uriComposer.ComposePicUri(catalogItem.PictureUri));
            var orderItem = new OrderItem(itemOrdered, basketItem.UnitPrice, basketItem.Quantity);
            return orderItem;
        }).ToList();

        var order = new Order(basket.BuyerId, shippingAddress, items);

        await _orderRepository.AddAsync(order);
        var orderMessage = JsonConvert.SerializeObject(new { id = Guid.NewGuid(), date = order.OrderDate.UtcDateTime.ToString(), details = JsonConvert.SerializeObject(order.OrderItems.Select(i => new { name = i.ItemOrdered.ProductName, qty = i.Units })) });
        //Send the order to DeliveryOrderProcessor function
        HttpClient httpclient = new HttpClient();
        HttpRequestMessage request = new HttpRequestMessage
        {
            Content = new StringContent(orderMessage, Encoding.UTF8, "application/json")
        };
        httpclient.DefaultRequestHeaders.Add("x-functions-key", "yQPxUXa0RHjnPEYfVofmeNhaJpquZIVN5npiO2EZDn7VqwEivUX7sA==");
        var response = await httpclient.PostAsync($"https://deliveryorderporcessorfunc.azurewebsites.net/api/DeliveryOrderProcessor", request.Content);
        //Send to event bus
        await using var sbclient = new ServiceBusClient("Endpoint=sb://eshopsb.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=wFMFyBA+9VrF0td4bA3+cwe048/r9Dj6NDwxTjWLMtU=");
        ServiceBusSender sender = sbclient.CreateSender("orderprocessor");
        try
        {
            var message = new ServiceBusMessage(orderMessage);
            await sender.SendMessageAsync(message);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"{DateTime.Now} :: Exception: {exception.Message}");
        }
        finally
        {
            await sender.DisposeAsync();
            await sbclient.DisposeAsync();
        }
    }
}
